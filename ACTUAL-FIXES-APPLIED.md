# Fix Summary - October 18, 2025

## Issues Identified from Testing

### Issue 1: FPS Values Don't Clear After Game Closes ✅ FIXED
**Problem:** When NMS closed, the FPS sensors still showed the last values (148 FPS) instead of resetting to 0.

**Root Cause:** Frame data collections were not being cleared when monitoring stopped, so old values persisted.

**Solution Applied:**
- Added `ClearFrameData()` method to reset all frame collections
- Called from `StopMonitoringAsync()` after process termination
- Clears: `_frameTimes`, `_gpuLatencies`, `_gpuTimes`, `_gpuBusyTimes`, `_gpuWaitTimes`, `_displayLatencies`, `_cpuBusyTimes`, `_cpuWaitTimes`

**Code Change:**
```csharp
private void ClearFrameData()
{
    lock (_frameTimes)
    {
        _frameTimes.Clear();
        _gpuLatencies.Clear();
        _gpuTimes.Clear();
        _gpuBusyTimes.Clear();
        _gpuWaitTimes.Clear();
        _displayLatencies.Clear();
        _cpuBusyTimes.Clear();
        _cpuWaitTimes.Clear();
        Console.WriteLine("PresentMon: Frame data cleared.");
    }
}
```

---

### Issue 2: Using Wrong PresentMon Executable ✅ FIXED
**Problem:** Using `PresentMon-2.3.1-x64-DLSS4.exe` causes access denied errors.

**Root Cause:** DLSS4 version has different permission requirements or anti-cheat conflicts.

**Solution Applied:**
- Changed to use standard `PresentMon-2.3.1-x64.exe` (matches old working version pattern)
- Updated cleanup to look for correct process name: `PresentMon-2.3.1-x64`

**Code Changes:**
```csharp
// Before:
string presentMonPath = Path.Combine(presentMonDir, "PresentMon-2.3.1-x64-DLSS4.exe");

// After:
string presentMonPath = Path.Combine(presentMonDir, "PresentMon-2.3.1-x64.exe");

// Also updated cleanup:
var processes = Process.GetProcessesByName("PresentMon-2.3.1-x64");
```

---

### Issue 3: Battlefield 6 Not Detected (NEEDS INVESTIGATION)
**Problem:** No fullscreen detection for Battlefield 6 visible in logs.

**Possible Causes:**
1. Game not actually running in true fullscreen (borderless window?)
2. Process name not what we expect (bf2042.exe? Battlefield.exe?)
3. Anti-cheat blocking window enumeration
4. Multi-monitor fullscreen detection tolerance too strict

**Investigation Needed:**
1. **Verify game is running:** Check Task Manager for exact process name
2. **Test fullscreen mode:** Ensure game is in true fullscreen, not borderless windowed
3. **Check window detection:** Look for any logs showing Battlefield window being enumerated
4. **Process name:** May need to add to detection or remove from blacklist

**Testing Steps:**
```powershell
# While Battlefield is running fullscreen:
Get-Process | Where-Object {$_.ProcessName -like "*battle*" -or $_.ProcessName -like "*bf*"}

# This will show the exact process name to look for
```

---

## What's Working Now

### ✅ No Man's Sky (NMS)
- **Detection:** Working perfectly
- **FPS Capture:** YES - showing ~148 FPS average
- **Data Flow:** Real-time CSV streaming via stdout
- **Process Termination:** Clean shutdown detected
- **Sensor Reset:** NOW FIXED - will clear on next test

**Log Evidence:**
```
Detected fullscreen window: No Man's Sky (PID: 55828, Process: NMS)
PresentMon: PresentMon process started successfully.
PresentMon: CSV Header: Application,ProcessID,SwapChainAddress...
Averaged: FrameTime=6.75ms, FPS=148.09, GpuLatency=0.44ms...
Process 55828 is no longer valid
PresentMon: stop requested.
```

### ❌ Battlefield 6
- **Detection:** NOT WORKING - no logs showing detection attempt
- **Possible Issue:** Process name unknown, not fullscreen, or blacklisted

---

## Next Testing Steps

### 1. Test Sensor Reset Fix
1. Launch InfoPanel
2. Start NMS in fullscreen
3. Verify FPS appears
4. Close NMS
5. **VERIFY:** FPS sensors reset to 0 (should see "PresentMon: Frame data cleared." in log)

### 2. Investigate Battlefield
1. Launch Battlefield 6
2. Ensure it's in **true fullscreen** (not borderless window)
3. Check debug.log for ANY window enumeration showing Battlefield
4. If no detection, get process name:
   ```powershell
   Get-Process | Where-Object {$_.MainWindowTitle -like "*Battlefield*"}
   ```
5. Check if process is in blacklist (FullscreenDetectionService.cs line 23-50)

### 3. Test Forever Winter
1. Same process as Battlefield
2. Check for fullscreen detection
3. Verify process name

---

## Files Modified

### `InfoPanel.Presentmon\Services\PresentMonService.cs`
- Line 81: Changed to use `PresentMon-2.3.1-x64.exe` instead of DLSS4 version
- Line 210: Added `ClearFrameData()` call in `StopMonitoringAsync()`
- Line 230-243: New `ClearFrameData()` method
- Line 509: Updated cleanup to look for correct process name

### Build & Deployment
- ✅ Built successfully
- ✅ Deployed to `C:\ProgramData\InfoPanel\plugins\InfoPanel.Presentmon`
- ✅ Using correct PresentMon executable

---

## Expected Log Output (Next Test)

### When NMS Starts:
```
Detected fullscreen window: No Man's Sky (PID: XXXXX, Process: NMS)
PresentMon: Running as administrator: True
PresentMon: Starting PresentMon with args: --process_id XXXXX --output_stdout...
PresentMon: PresentMon process started successfully.
PresentMon: CSV Header: Application,ProcessID,...
Averaged: FrameTime=X.XXms, FPS=XXX.XX,...
```

### When NMS Closes:
```
Process XXXXX is no longer valid
PresentMon: stop requested.
PresentMon: Terminating PresentMon process (PID: XXXXX)...
PresentMon: Frame data cleared.                    ← NEW!
PresentMon: monitoring stopped.
```

### When Battlefield Starts (IF it works):
```
Detected fullscreen window: Battlefield (PID: XXXXX, Process: <ProcessName>)
PresentMon: Running as administrator: True
...
```

### If Battlefield Doesn't Detect:
```
(No logs at all, or)
Window HWND: ..., Process: <battlefield>, Fullscreen: False
```

---

## Known Limitations

1. **True Fullscreen Only:** Game must be in true fullscreen mode, not borderless windowed
2. **ETW Permissions:** Some games with anti-cheat may block PresentMon's ETW tracing
3. **Administrator Required:** InfoPanel must run as admin for PresentMon to access ETW
4. **Process Detection:** If game process is in blacklist or has weird window properties, won't detect

---

## If Battlefield Still Doesn't Work

### Debugging Checklist:
- [ ] Confirm game is in true fullscreen (Alt+Enter to toggle)
- [ ] Get exact process name from Task Manager
- [ ] Check if process appears in window enumeration logs
- [ ] Verify process is not in blacklist array
- [ ] Test with older PresentMon version (1.10.0) if needed
- [ ] Check for anti-cheat blocking ETW
- [ ] Try disabling Easy Anti-Cheat temporarily

### Alternative Approach:
If fullscreen detection fails, we could add a **manual process selection** feature:
1. User specifies process name in config
2. Plugin monitors that process even if not fullscreen
3. Bypasses fullscreen detection entirely

---

## Apologies for Earlier Confusion

**You were 100% correct:**
- The `InfoPanel.FPS` backup folder was NOT being loaded by InfoPanel (wrong location)
- The DLSS4 executable DOES have access issues
- I should have checked the actual debug.log output first instead of making assumptions

**What I learned:**
- Always verify folder structure before assuming plugin conflicts
- Check debug logs FIRST to see what's actually happening
- Listen to user feedback about access denied issues

Thank you for the patience and clarification! The fixes are now deployed and should work correctly.
