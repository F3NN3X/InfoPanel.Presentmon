# BREAKTHROUGH: How PresentMonDataProvider Actually Works

## Critical Discovery from Binary Strings

### Command Line Format Strings:
```c
"%s -session_name PresentMonDataProvider -stop_existing_session -process_id %d -output_stdout -qpc_time"
"%s -session_name PresentMonDataProvider -stop_existing_session -process_id %d -output_stdout -track_gpu -qpc_time"
```

### Shared Memory Names:
```
PMDPSharedMemory      ← PresentMonDataProvider writes here
RTSSSharedMemoryV2    ← RTSS reads from here
```

### Windows API Calls Found:
- `CreatePipe` - For capturing stdout
- `CreateFileMappingA` - For shared memory
- `FindWindowA` / `FindWindowExA` - For window management
- `RegisterWindowMessageA` - For custom window messages
- `GetForegroundWindow` - For detecting active application

## How It Actually Works

### Architecture:
```
RTSS (Main Application)
    ↓ (Launches)
PresentMonDataProvider.exe (GUI App, runs continuously)
    ↓ (Reads config)
PresentMonDataProvider.cfg
    ↓ (Launches with formatted args)
PresentMon-*.exe --session_name PresentMonDataProvider --process_id X --output_stdout
    ↓ (CSV data to stdout)
PresentMonDataProvider (captures stdout via pipe)
    ↓ (Parses and writes to)
PMDPSharedMemory (Shared memory object)
    ↓ (Reads from)
RTSS (Displays FPS overlay)
```

### PresentMonDataProvider.exe Behavior:

1. **Launched WITHOUT command-line arguments** by RTSS
2. **Runs as a GUI application** (has window message loop)
3. **Reads PresentMonDataProvider.cfg** to determine:
   - Which PresentMon executable to use (`ConsolePath`)
   - Additional arguments (`ConsoleCmd`)
   - Timing parameters (`EtwFlushPeriod`, `SleepPeriod`, `BurstDelay`)
4. **Detects active/foreground window** using `GetForegroundWindow()`
5. **Gets process ID** of foreground application
6. **Launches PresentMon.exe** with formatted command:
   ```
   PresentMon-2.3.1-x64.exe -session_name PresentMonDataProvider -stop_existing_session -process_id <PID> --output_stdout -qpc_time
   ```
7. **Captures stdout** via pipe (`CreatePipe`)
8. **Parses CSV data** line by line
9. **Writes to shared memory** (`PMDPSharedMemory`) so RTSS can read it
10. **Runs continuously** until terminated

## Why Our Approach Failed

### What We Tried:
```csharp
Process.Start("PresentMonDataProvider.exe", "--process_id 12345 --output_stdout");
```

### Why It Failed:
- ❌ PresentMonDataProvider.exe **doesn't accept command-line arguments**
- ❌ It expects to run as a **GUI application with a message loop**
- ❌ It manages PresentMon lifecycle internally
- ❌ It outputs to **shared memory**, not stdout

### What Happened:
```log
Process started: PresentMonDataProvider.exe --process_id 15496 ...
PresentMon: ReadOutputAsync loop exited.  ← Exited immediately
PresentMon: Process exited with code: 0   ← Clean exit (not an error!)
```

It saw unknown arguments, did nothing, and exited cleanly.

## The Correct Approach for Our Use Case

### Option 1: Use PresentMonDataProvider's Shared Memory (RECOMMENDED)

**How to implement:**
1. **Configure PresentMonDataProvider.cfg** with desired PresentMon executable
2. **Launch PresentMonDataProvider.exe WITHOUT arguments**
3. **Let it run continuously** in the background
4. **Open the shared memory** using Windows API:
   ```csharp
   var sharedMem = MemoryMappedFile.OpenExisting("PMDPSharedMemory");
   ```
5. **Read FPS data** from the shared memory structure
6. **Update sensors** based on shared memory content

**Implementation:**
```csharp
public class PresentMonService
{
    private Process? _providerProcess;
    private MemoryMappedFile? _sharedMemory;
    private MemoryMappedViewAccessor? _accessor;
    
    public async Task<bool> OpenSessionAsync(int pid)
    {
        // Step 1: Update config file with target process (if needed)
        // Note: Provider detects foreground window automatically
        
        // Step 2: Launch PresentMonDataProvider.exe WITHOUT args
        _providerProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "PresentMonDataProvider.exe",
                WorkingDirectory = providerDir,
                CreateNoWindow = true,
                UseShellExecute = false
                // NO ARGUMENTS!
            }
        };
        
        _providerProcess.Start();
        
        // Step 3: Wait for it to initialize
        await Task.Delay(1000);
        
        // Step 4: Open shared memory
        _sharedMemory = MemoryMappedFile.OpenExisting("PMDPSharedMemory");
        _accessor = _sharedMemory.CreateViewAccessor();
        
        // Step 5: Start polling shared memory
        _pollingTask = Task.Run(() => PollSharedMemoryAsync(ct));
        
        return true;
    }
    
    private async Task PollSharedMemoryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Read FPS data from shared memory
            // Structure needs to be determined from RTSS SDK or reverse engineering
            
            await Task.Delay(16); // Poll at ~60Hz
        }
    }
}
```

### Option 2: Call PresentMon.exe Directly (Simpler, but needs permissions)

Since PresentMonDataProvider just wraps PresentMon.exe, we can:
1. **Add user to "Performance Log Users" group**
2. **Logoff and logon** to refresh security token
3. **Call PresentMon.exe directly** with our existing code

This avoids the complexity of shared memory but requires system configuration.

## Shared Memory Structure (Needs Research)

We need to determine the structure of `PMDPSharedMemory`:
- Size of the memory region
- Data structure layout
- Which fields contain FPS/frame time
- How to detect when data is updated

Possible approaches:
1. **Check RTSS SDK** if available
2. **Reverse engineer** by examining memory in a debugger
3. **Contact RTSS/PresentMon developers** for documentation

## Configuration File Usage

**PresentMonDataProvider.cfg:**
```ini
[Settings]
ConsoleMode     = 1                            # 1 = Console output capture mode
ConsolePath     = PresentMon-2.3.1-x64.exe    # Which executable to launch
ConsoleCmd      = --output_stdout              # Additional args for PresentMon
EtwFlushPeriod  = 8                            # ETW flush interval (ms)
SleepPeriod     = 100                          # Polling interval (ms)
BurstDelay      = 2500                         # Delay between bursts (ms)
```

The provider reads this config to determine:
- Which PresentMon version to use
- What arguments to pass
- Timing parameters for data collection

## Summary

**PresentMonDataProvider.exe is a GUI application** that:
- ✅ Runs continuously
- ✅ Manages PresentMon.exe lifecycle
- ✅ Captures stdout and parses CSV
- ✅ Writes data to shared memory
- ✅ Designed for RTSS integration

**It is NOT:**
- ❌ A command-line tool
- ❌ A direct PresentMon wrapper
- ❌ Something that outputs to stdout

**To use it, we must:**
1. Launch it without arguments
2. Let it run continuously
3. Read from `PMDPSharedMemory` shared memory
4. Parse the shared memory structure

This is significantly more complex than we anticipated, which is why the simpler approach of calling PresentMon.exe directly (with proper permissions) might be more practical for our use case.
