using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.ServiceProcess;
using System.Security.Principal;
using InfoPanel.Presentmon.Models;

namespace InfoPanel.Presentmon.Services
{
    // Shared memory structure for PMDPSharedMemory
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PMDPSharedMemoryHeader
    {
        public uint Signature;          // 'PMDP' or similar
        public uint Version;            // Structure version
        public uint DataSize;           // Size of data section
        public uint FrameCount;         // Number of frames captured
        public float CurrentFPS;        // Current FPS
        public float AverageFrameTime;  // Average frame time in ms
        public float MinFrameTime;      // Min frame time
        public float MaxFrameTime;      // Max frame time
        public float Percentile1Low;    // 1% low FPS
        public float Percentile01Low;   // 0.1% low FPS
        public uint ProcessId;          // Target process ID
        public uint Reserved1;
        public uint Reserved2;
        public uint Reserved3;
    }

    public class PresentMonService : IDisposable
    {
        public event EventHandler<FrameData>? MetricsUpdated;

        // Windows API imports for communicating with PresentMonDataProvider window
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
        
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);
        
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private Process? _presentMonProcess;
        private Process? _providerProcess;
        private CancellationTokenSource? _pollingCts;
        private Task? _pollingTask;

        // Shared memory for reading PresentMon data
        private MemoryMappedFile? _sharedMemory;
        private MemoryMappedViewAccessor? _sharedMemoryAccessor;
        
        // RTSS emulation service
        private RtssEmulationService? _rtssEmulation;

        // Frame data collections for averaging
        private readonly List<float> _frameTimes = new();
        private readonly List<float> _gpuLatencies = new();
        private readonly List<float> _gpuTimes = new();
        private readonly List<float> _gpuBusyTimes = new();
        private readonly List<float> _gpuWaitTimes = new();
        private readonly List<float> _displayLatencies = new();
        private readonly List<float> _cpuBusyTimes = new();
        private readonly List<float> _cpuWaitTimes = new();
        private const int MaxFrameSamples = 1000;

        public PresentMonService()
        {
        }

        private static readonly string[] FrameTimeColumnCandidates =
        {
            "FrameTime",
            "frame_time",
            "FrameTimeMs",
            "frame_time_ms",
            "MsBetweenPresents",
            "msBetweenPresents",
            "msBetweenDisplayChange",
            "MsRenderPresentLatency",
            "MsBetweenAppStart",
            "MsBetweenSimulationStart",
            "MsAllInputToPhotonLatency",
            "MsClickToPhotonLatency",
            "MsGPUTime",
            "msGPUActive",
            "MsGPULatency",
            "MsGPUBusy",
            "MsGPUWait",
            "MsCPUBusy",
            "MsCPUWait",
            "MsInPresentAPI",
            "msInPresentAPI",
            "MsUntilDisplayed",
            "msUntilDisplayed",
            "msUntilRenderComplete",
            "msUntilRenderStart",
            "msSinceInput"
        };

        public async Task<bool> OpenSessionAsync(int pid)
        {
            try
            {
                Console.WriteLine($"PresentMon: Running as administrator: {IsRunningAsAdministrator()}");

                // Clean up any existing PresentMon processes
                await CleanupExistingProcessesAsync().ConfigureAwait(false);

                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                string presentMonDir = Path.Combine(pluginDir, "PresentMonDataProvider");
                string providerPath = Path.Combine(presentMonDir, "PresentMonDataProvider.exe");

                if (!File.Exists(providerPath))
                {
                    Console.WriteLine($"PresentMon: PresentMonDataProvider.exe not found at: {providerPath}");
                    return false;
                }

                // STEP 0: Create fake RTSS environment to prevent provider from exiting
                Console.WriteLine("PresentMon: Creating fake RTSS environment...");
                _rtssEmulation = new RtssEmulationService();
                bool rtssCreated = _rtssEmulation.CreateFakeRtssMemory();
                
                if (!rtssCreated)
                {
                    Console.WriteLine("PresentMon: Warning - Could not create fake RTSS memory");
                    // Continue anyway - maybe it's already running
                }

                // STEP 1: Launch PresentMonDataProvider.exe WITHOUT arguments
                // It will run as a GUI application with a hidden window
                Console.WriteLine($"PresentMon: Launching PresentMonDataProvider.exe (GUI mode, no arguments)...");
                _providerProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = providerPath,
                        WorkingDirectory = presentMonDir,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = false,
                        RedirectStandardError = false
                    },
                    EnableRaisingEvents = true
                };

                _providerProcess.Exited += (s, e) =>
                {
                    Console.WriteLine($"PresentMon: PresentMonDataProvider exited with code: {_providerProcess?.ExitCode ?? -1}");
                };

                _providerProcess.Start();
                Console.WriteLine($"PresentMon: PresentMonDataProvider started (PID: {_providerProcess.Id})");

                // STEP 2: Wait for the provider to initialize its window
                // Give it time to create the "PresentMonDataProviderConnectWnd" window
                await Task.Delay(1000).ConfigureAwait(false);

                // STEP 3: Send Windows message to tell it which process to monitor
                bool messageSent = await SendTargetProcessMessageAsync(pid).ConfigureAwait(false);
                if (!messageSent)
                {
                    Console.WriteLine("PresentMon: Failed to send target process message to provider.");
                    await StopMonitoringAsync().ConfigureAwait(false);
                    return false;
                }

                Console.WriteLine($"PresentMon: Successfully configured provider to monitor PID {pid}");

                // STEP 4: Open shared memory for reading
                // PresentMonDataProvider writes to "PMDPSharedMemory"
                try
                {
                    Console.WriteLine("PresentMon: Opening shared memory 'PMDPSharedMemory'...");
                    _sharedMemory = MemoryMappedFile.OpenExisting("PMDPSharedMemory", MemoryMappedFileRights.Read);
                    _sharedMemoryAccessor = _sharedMemory.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    Console.WriteLine("PresentMon: Shared memory opened successfully");
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine("PresentMon: Shared memory 'PMDPSharedMemory' not found. Waiting for provider to create it...");
                    
                    // Wait up to 5 seconds for shared memory to be created
                    for (int i = 0; i < 50; i++)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                        try
                        {
                            _sharedMemory = MemoryMappedFile.OpenExisting("PMDPSharedMemory", MemoryMappedFileRights.Read);
                            _sharedMemoryAccessor = _sharedMemory.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                            Console.WriteLine("PresentMon: Shared memory opened successfully after waiting");
                            break;
                        }
                        catch (FileNotFoundException)
                        {
                            // Keep waiting
                        }
                    }
                    
                    if (_sharedMemory == null)
                    {
                        Console.WriteLine("PresentMon: Failed to open shared memory after 5 seconds");
                        await StopMonitoringAsync().ConfigureAwait(false);
                        return false;
                    }
                }

                // STEP 5: Start polling shared memory
                _pollingCts = new CancellationTokenSource();
                _pollingTask = Task.Run(() => ReadSharedMemoryAsync(_pollingCts.Token));

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: Session setup failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Sends a Windows message to PresentMonDataProvider to set the target process ID.
        /// This uses the same approach as RTSS - finding the hidden window and sending UM_SET_TARGET_PROCESS message.
        /// </summary>
        private async Task<bool> SendTargetProcessMessageAsync(int pid)
        {
            // Register the custom Windows message that PresentMonDataProvider listens for
            uint msg = RegisterWindowMessage("UM_SET_TARGET_PROCESS");
            if (msg == 0)
            {
                int error = Marshal.GetLastWin32Error();
                Console.WriteLine($"PresentMon: Failed to register UM_SET_TARGET_PROCESS message. Error: {error}");
                return false;
            }

            Console.WriteLine($"PresentMon: Registered message UM_SET_TARGET_PROCESS = {msg}");

            // Find the provider's window - it creates a window with class "PresentMonDataProviderConnectWnd"
            // Retry for up to 5 seconds in case the window isn't created yet
            for (int i = 0; i < 50; i++)
            {
                IntPtr hwnd = FindWindow("PresentMonDataProviderConnectWnd", null);
                if (hwnd != IntPtr.Zero)
                {
                    Console.WriteLine($"PresentMon: Found PresentMonDataProvider window (HWND: 0x{hwnd:X})");
                    
                    // Send the message with the process ID as wParam
                    IntPtr result = SendMessage(hwnd, msg, (IntPtr)pid, IntPtr.Zero);
                    Console.WriteLine($"PresentMon: Sent UM_SET_TARGET_PROCESS message for PID {pid}, result: {result}");
                    return true;
                }

                // Wait 100ms and try again
                await Task.Delay(100).ConfigureAwait(false);
            }

            Console.WriteLine("PresentMon: Failed to find PresentMonDataProviderConnectWnd window after 5 seconds.");
            return false;
        }

        private async Task ReadSharedMemoryAsync(CancellationToken ct)
        {
            try
            {
                Console.WriteLine("PresentMon: Starting shared memory polling loop...");
                
                if (_sharedMemoryAccessor == null)
                {
                    Console.WriteLine("PresentMon: Shared memory accessor is null");
                    return;
                }

                long lastFrameCount = 0;
                int pollIntervalMs = 16; // Poll at ~60Hz

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Read header from shared memory
                        // Note: We don't know the exact structure yet, so we'll try a simple approach
                        // Read first 256 bytes to analyze the structure
                        byte[] buffer = new byte[256];
                        _sharedMemoryAccessor.ReadArray(0, buffer, 0, buffer.Length);

                        // Log raw data for analysis (first time only)
                        if (lastFrameCount == 0)
                        {
                            Console.WriteLine($"PresentMon: Shared memory raw data (first 64 bytes):");
                            Console.WriteLine($"  {BitConverter.ToString(buffer, 0, 64)}");
                            
                            // Try to interpret as floats
                            Console.WriteLine("PresentMon: Interpreting as floats:");
                            for (int i = 0; i < 16; i++)
                            {
                                float value = BitConverter.ToSingle(buffer, i * 4);
                                Console.WriteLine($"  Offset {i*4:D3}: {value}");
                            }
                        }

                        // Try reading at different offsets to find FPS data
                        // Common patterns: FPS values are usually between 0-300
                        for (int offset = 0; offset < 128; offset += 4)
                        {
                            float value = BitConverter.ToSingle(buffer, offset);
                            
                            // Check if this looks like an FPS value (reasonable range)
                            if (value > 0 && value < 500 && !float.IsNaN(value) && !float.IsInfinity(value))
                            {
                                // This might be FPS data
                                if (lastFrameCount == 0 || value != lastFrameCount)
                                {
                                    Console.WriteLine($"PresentMon: Possible FPS at offset {offset}: {value:F1}");
                                    
                                    // Create frame data from this value
                                    var frameData = new FrameData
                                    {
                                        Fps = value,
                                        FrameTimeMs = value > 0 ? 1000f / value : 0,
                                        GpuLatencyMs = 0,
                                        GpuTimeMs = 0,
                                        GpuBusyMs = 0,
                                        GpuWaitMs = 0,
                                        DisplayLatencyMs = 0,
                                        CpuBusyMs = 0,
                                        CpuWaitMs = 0,
                                        OnePercentLowFps = 0,
                                        ZeroPointOnePercentLowFps = 0,
                                        GpuUtilizationPercent = 0
                                    };

                                    MetricsUpdated?.Invoke(this, frameData);
                                    lastFrameCount = (long)value;
                                    break; // Found data, move to next poll
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon: Error reading shared memory: {ex.Message}");
                    }

                    await Task.Delay(pollIntervalMs, ct).ConfigureAwait(false);
                }

                Console.WriteLine("PresentMon: Shared memory polling loop exited.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("PresentMon: Shared memory polling cancelled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: Shared memory polling error: {ex.Message}");
            }
        }

        private async Task ReadOutputAsync(CancellationToken ct)
        {
            try
            {
                // PresentMonDataProvider writes to a file, not stdout
                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
                string presentMonDir = Path.Combine(pluginDir, "PresentMonDataProvider");
                string outputPath = Path.Combine(presentMonDir, "PresentMon-Output.csv");

                Console.WriteLine($"PresentMon: Monitoring CSV file: {outputPath}");

                // Wait for the file to be created
                for (int i = 0; i < 50 && !ct.IsCancellationRequested; i++)
                {
                    if (File.Exists(outputPath))
                    {
                        Console.WriteLine("PresentMon: Output file found!");
                        break;
                    }
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }

                if (!File.Exists(outputPath))
                {
                    Console.WriteLine("PresentMon: Output file not created after 5 seconds.");
                    return;
                }

                // Read the file continuously, like "tail -f"
                bool headerSkipped = false;
                long lastPosition = 0;

                using (FileStream fs = new FileStream(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader reader = new StreamReader(fs))
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // Check if file has grown
                        if (fs.Length > lastPosition)
                        {
                            fs.Seek(lastPosition, SeekOrigin.Begin);
                            
                            string? line;
                            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                            {
                                // Skip CSV header
                                if (!headerSkipped)
                                {
                                    if (line.StartsWith("Application"))
                                    {
                                        headerSkipped = true;
                                        Console.WriteLine($"PresentMon: CSV Header: {line}");
                                    }
                                    continue;
                                }

                                // Process data line
                                ProcessOutputLine(line);
                            }

                            lastPosition = fs.Position;
                        }

                        // Poll every 100ms
                        await Task.Delay(100, ct).ConfigureAwait(false);
                    }
                }

                Console.WriteLine("PresentMon: ReadOutputAsync loop exited.");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("PresentMon: ReadOutputAsync cancelled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: ReadOutputAsync error: {ex.Message}");
            }
        }

        public async Task StopMonitoringAsync()
        {
            try
            {
                Console.WriteLine("PresentMon: stop requested.");

                _pollingCts?.Cancel();
                if (_pollingTask != null)
                {
                    await _pollingTask.ConfigureAwait(false);
                }

                // Kill PresentMonDataProvider process if running
                if (_providerProcess != null && !_providerProcess.HasExited)
                {
                    try
                    {
                        Console.WriteLine($"PresentMon: Terminating PresentMonDataProvider process (PID: {_providerProcess.Id})...");
                        _providerProcess.Kill();
                        await _providerProcess.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon: Failed to terminate provider: {ex.Message}");
                    }
                    finally
                    {
                        _providerProcess?.Dispose();
                        _providerProcess = null;
                    }
                }

                // Also kill any orphaned PresentMon.exe that was launched by the provider
                if (_presentMonProcess != null && !_presentMonProcess.HasExited)
                {
                    try
                    {
                        Console.WriteLine($"PresentMon: Terminating PresentMon process (PID: {_presentMonProcess.Id})...");
                        _presentMonProcess.Kill();
                        await _presentMonProcess.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon: Failed to terminate process: {ex.Message}");
                    }
                    finally
                    {
                        _presentMonProcess?.Dispose();
                        _presentMonProcess = null;
                    }
                }

                // Clear all frame data collections
                ClearFrameData();

                // Also clean up any orphaned PresentMon processes
                await CleanupExistingProcessesAsync().ConfigureAwait(false);

                Console.WriteLine("PresentMon: monitoring stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error during stop. {ex.Message}");
            }
        }

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

        private void ProcessOutputLine(string line)
        {
            string[] parts = line.Split(',');
            if (parts.Length < 17) // Need at least up to DisplayLatency
            {
                Console.WriteLine($"Invalid CSV line: {line}");
                return;
            }

            // Console.WriteLine($"Raw frame data: {line}"); // Commented out to reduce verbosity

            lock (_frameTimes)
            {
                if (float.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out float frameTimeMs)) // MsBetweenPresents
                {
                    _frameTimes.Add(frameTimeMs);
                    if (_frameTimes.Count > MaxFrameSamples)
                        _frameTimes.RemoveAt(0);

                    float avgFrameTime = _frameTimes.Average();
                    float fps = avgFrameTime > 0 ? 1000f / avgFrameTime : 0;

                    // Calculate 1% and 0.1% low FPS using percentiles
                    float onePercentLowFps = fps;
                    float zeroPointOnePercentLowFps = fps;
                    if (_frameTimes.Count >= MaxFrameSamples)
                    {
                        var sortedFrameTimes = _frameTimes.OrderByDescending(t => t).ToList(); // Descending: higher = worse
                        int onePercentIndex = (int)(MaxFrameSamples * 0.01) - 1; // 1% worst = top 1%, adjust for 0-based index
                        int zeroPointOnePercentIndex = (int)(MaxFrameSamples * 0.001) - 1; // 0.1% worst = top 0.1%
                        onePercentIndex = Math.Max(0, onePercentIndex); // Ensure non-negative
                        zeroPointOnePercentIndex = Math.Max(0, zeroPointOnePercentIndex);
                        float onePercentLowFrameTime = sortedFrameTimes[onePercentIndex]; // e.g., 10th worst for 1000
                        float zeroPointOnePercentLowFrameTime = sortedFrameTimes[zeroPointOnePercentIndex]; // e.g., 1st worst
                        onePercentLowFps = onePercentLowFrameTime > 0 ? 1000f / onePercentLowFrameTime : 0;
                        zeroPointOnePercentLowFps = zeroPointOnePercentLowFrameTime > 0 ? 1000f / zeroPointOnePercentLowFrameTime : 0;
                    }

                    float gpuLatency = 0, gpuTime = 0, gpuBusy = 0, gpuWait = 0, displayLatency = 0, cpuBusy = 0, cpuWait = 0;
                    if (float.TryParse(parts[20], NumberStyles.Float, CultureInfo.InvariantCulture, out gpuLatency) && // MsGPULatency
                        float.TryParse(parts[21], NumberStyles.Float, CultureInfo.InvariantCulture, out gpuTime) &&   // MsGPUTime
                        float.TryParse(parts[22], NumberStyles.Float, CultureInfo.InvariantCulture, out gpuBusy) &&   // MsGPUBusy
                        float.TryParse(parts[23], NumberStyles.Float, CultureInfo.InvariantCulture, out gpuWait) &&   // MsGPUWait
                        float.TryParse(parts[16], NumberStyles.Float, CultureInfo.InvariantCulture, out displayLatency) && // CPUStartTimeInMs (placeholder)
                        float.TryParse(parts[18], NumberStyles.Float, CultureInfo.InvariantCulture, out cpuBusy) &&   // MsCPUBusy
                        float.TryParse(parts[19], NumberStyles.Float, CultureInfo.InvariantCulture, out cpuWait))     // MsCPUWait
                    {
                        _gpuLatencies.Add(gpuLatency);
                        _gpuTimes.Add(gpuTime);
                        _gpuBusyTimes.Add(gpuBusy);
                        _gpuWaitTimes.Add(gpuWait);
                        _displayLatencies.Add(displayLatency);
                        _cpuBusyTimes.Add(cpuBusy);
                        _cpuWaitTimes.Add(cpuWait);

                        if (_gpuLatencies.Count > MaxFrameSamples)
                        {
                            _gpuLatencies.RemoveAt(0);
                            _gpuTimes.RemoveAt(0);
                            _gpuBusyTimes.RemoveAt(0);
                            _gpuWaitTimes.RemoveAt(0);
                            _displayLatencies.RemoveAt(0);
                            _cpuBusyTimes.RemoveAt(0);
                            _cpuWaitTimes.RemoveAt(0);
                        }

                        float avgGpuTime = _gpuTimes.Average();
                        float gpuUtilization = avgFrameTime > 0 ? (avgGpuTime / avgFrameTime) * 100f : 0f;

                        var frameData = new FrameData
                        {
                            Fps = fps,
                            FrameTimeMs = avgFrameTime,
                            OnePercentLowFps = onePercentLowFps,
                            ZeroPointOnePercentLowFps = zeroPointOnePercentLowFps,
                            GpuLatencyMs = _gpuLatencies.Average(),
                            GpuTimeMs = avgGpuTime,
                            GpuBusyMs = _gpuBusyTimes.Average(),
                            GpuWaitMs = _gpuWaitTimes.Average(),
                            DisplayLatencyMs = _displayLatencies.Average(),
                            CpuBusyMs = _cpuBusyTimes.Average(),
                            CpuWaitMs = _cpuWaitTimes.Average(),
                            GpuUtilizationPercent = gpuUtilization
                        };

                        MetricsUpdated?.Invoke(this, frameData);

                        Console.WriteLine($"Averaged: FrameTime={avgFrameTime:F2}ms, FPS={fps:F2}, " +
                                        $"GpuLatency={_gpuLatencies.Average():F2}ms, GpuTime={avgGpuTime:F2}ms, " +
                                        $"GpuBusy={_gpuBusyTimes.Average():F2}ms, GpuWait={_gpuWaitTimes.Average():F2}ms, " +
                                        $"DisplayLatency={_displayLatencies.Average():F2}ms, " +
                                        $"CpuBusy={_cpuBusyTimes.Average():F2}ms, CpuWait={_cpuWaitTimes.Average():F2}ms, " +
                                        $"GpuUtilization={gpuUtilization:F1}%, " +
                                        $"1%Low={onePercentLowFps:F2}FPS, 0.1%Low={zeroPointOnePercentLowFps:F2}FPS");
                    }
                }
                else
                {
                    Console.WriteLine($"Failed to parse FrameTime: {line}");
                }
            }
        }

        private async void PollingLoopAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("PresentMon: monitoring started.");
            
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            string presentMonDir = Path.Combine(pluginDir, "PresentMonDataProvider");
            string csvPath = Path.Combine(presentMonDir, "bf6-presentmon.csv");
            long lastPosition = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(csvPath))
                        {
                            using var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                            if (fs.Length > lastPosition)
                            {
                                fs.Seek(lastPosition, SeekOrigin.Begin);
                                using var reader = new StreamReader(fs);
                                string? line;
                                while ((line = reader.ReadLine()) != null)
                                {
                                    if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("Application"))
                                    {
                                        ProcessOutputLine(line);
                                    }
                                }
                                lastPosition = fs.Position;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon: Error reading CSV file: {ex.Message}");
                    }

                    await Task.Delay(100, cancellationToken);
                }
            }
            catch (TaskCanceledException)
            {
                // Expected when cancellation is requested
                Console.WriteLine("PresentMon: monitoring cancelled.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error in monitoring loop: {ex.Message}");
            }
        }

        private static async Task<bool> StartAsync(Process process)
        {
            var tcs = new TaskCompletionSource<bool>();
            process.EnableRaisingEvents = true;
            process.Exited += (s, e) =>
            {
                if (!tcs.Task.IsCompleted)
                    tcs.TrySetResult(process.ExitCode == 0);
            };
            try
            {
                if (!process.Start())
                {
                    Console.WriteLine($"Process failed to start: {process.StartInfo.FileName} {process.StartInfo.Arguments}");
                    tcs.TrySetResult(false);
                    return await tcs.Task;
                }
                Console.WriteLine($"Process started: {process.StartInfo.FileName} {process.StartInfo.Arguments}, PID: {process.Id}");
                tcs.TrySetResult(true);
            }
            catch (Win32Exception ex)
            {
                Console.WriteLine($"Failed to start process {process.StartInfo.FileName}: {ex} (Error Code: {ex.NativeErrorCode})");
                tcs.TrySetResult(false);
            }
            return await tcs.Task;
        }

        private async Task ExecuteCommandAsync(string fileName, string arguments, int timeoutMs, bool logOutput, CancellationToken cancellationToken = default)
        {
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = arguments,
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = logOutput,
                        RedirectStandardError = logOutput
                    }
                };

                bool started = await StartAsync(proc).ConfigureAwait(false);
                if (!started)
                {
                    Console.WriteLine($"Command {fileName} {arguments} failed to start.");
                    return;
                }

                if (logOutput)
                {
                    var exitTask = proc.WaitForExitAsync(cancellationToken);
                    var delayTask = Task.Delay(timeoutMs, cancellationToken);
                    if (await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false) == exitTask)
                    {
                        string output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                        string error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                        if (proc.ExitCode == 0)
                            Console.WriteLine($"{fileName} {arguments}: {output}");
                        else
                        {
                            Console.WriteLine($"{fileName} {arguments} failed: {error}");
                            throw new Exception($"{fileName} {arguments} failed with exit code {proc.ExitCode}: {error}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"{fileName} {arguments} timed out after {timeoutMs}ms.");
                        try
                        {
                            proc.Kill(true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to kill process {fileName}: {ex.Message}");
                        }
                    }
                }
                else
                {
                    var exitTask = proc.WaitForExitAsync(cancellationToken);
                    var delayTask = Task.Delay(timeoutMs, cancellationToken);
                    if (await Task.WhenAny(exitTask, delayTask).ConfigureAwait(false) != exitTask)
                    {
                        Console.WriteLine($"{fileName} {arguments} timed out after {timeoutMs}ms.");
                        try
                        {
                            proc.Kill(true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Failed to kill process {fileName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to execute {fileName} {arguments}: {ex.Message}");
            }
        }

        private static bool IsRunningAsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        private async Task CleanupExistingProcessesAsync()
        {
            try
            {
                // Kill any existing PresentMonDataProvider processes
                var providerProcesses = Process.GetProcessesByName("PresentMonDataProvider");
                foreach (var proc in providerProcesses)
                {
                    try
                    {
                        Console.WriteLine($"Terminating existing PresentMonDataProvider process (PID: {proc.Id})...");
                        proc.Kill();
                        await proc.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to terminate process {proc.Id}: {ex.Message}");
                    }
                }

                // Also kill any orphaned PresentMon-2.3.1-x64 processes (launched by provider)
                var presentMonProcesses = Process.GetProcessesByName("PresentMon-2.3.1-x64");
                foreach (var proc in presentMonProcesses)
                {
                    try
                    {
                        Console.WriteLine($"Terminating existing PresentMon process (PID: {proc.Id})...");
                        proc.Kill();
                        await proc.WaitForExitAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to terminate process {proc.Id}: {ex.Message}");
                    }
                }

                // Clean up any existing ETW sessions with various possible names
                await ExecuteCommandAsync("logman.exe", "stop PresentMon -ets", 5000, false).ConfigureAwait(false);
                await ExecuteCommandAsync("logman.exe", "stop PresentMon_* -ets", 5000, false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: Cleanup failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _pollingCts?.Cancel();
                _pollingTask?.Wait(5000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error stopping polling during dispose. {ex.Message}");
            }

            _pollingCts?.Dispose();
            
            // Clean up shared memory
            _sharedMemoryAccessor?.Dispose();
            _sharedMemory?.Dispose();
            
            // Clean up RTSS emulation
            _rtssEmulation?.Dispose();
        }
    }
}
