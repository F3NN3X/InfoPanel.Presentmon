using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Presentmon.Models;
using Vanara.PInvoke;

namespace InfoPanel.Presentmon.Services
{
    /// <summary>
    /// Service that manages PresentMonDataProvider.exe and reads FPS data via shared memory
    /// Implements RTSS emulation to keep the provider running
    /// </summary>
    public class PresentMonProviderService : IDisposable
    {
        public event EventHandler<FrameData>? MetricsUpdated;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string lpString);

        private static uint? _umSetTargetProcess;
        private static uint UM_SET_TARGET_PROCESS
        {
            get
            {
                if (_umSetTargetProcess == null)
                {
                    _umSetTargetProcess = RegisterWindowMessage("UM_SET_TARGET_PROCESS");
                    Console.WriteLine($"PresentMonProvider: Registered UM_SET_TARGET_PROCESS = 0x{_umSetTargetProcess:X}");
                }
                return _umSetTargetProcess.Value;
            }
        }

        private const string CONNECT_WND_NAME = "PresentMonDataProviderConnectWnd";

        private Process? _providerProcess;
        private MemoryMappedFile? _pmdpSharedMemory;
        private CancellationTokenSource? _pollingCts;
        private Task? _pollingTask;
        private uint _targetProcessId = 0;
        private int _frameCounter = 0;
        private uint _lastFrameCount = 0;
        private int _staleFrameCounter = 0;
        private const int MAX_STALE_READS = 10; // After 10 reads with no new frames, consider data stale
        private readonly string _providerPath;

        public PresentMonProviderService()
        {
            // Get path to PresentMonDataProvider.exe in plugin directory
            string pluginDirectory = Path.GetDirectoryName(typeof(PresentMonProviderService).Assembly.Location) 
                ?? AppDomain.CurrentDomain.BaseDirectory;
            string presentMonFolder = Path.Combine(pluginDirectory, "PresentMonDataProvider");
            _providerPath = Path.Combine(presentMonFolder, "PresentMonDataProvider.exe");

            Console.WriteLine($"PresentMonProvider: Provider path: {_providerPath}");
        }

        public async Task<bool> StartMonitoringAsync(int pid)
        {
            try
            {
                // If already monitoring this PID, return success
                if (_targetProcessId == (uint)pid && _providerProcess != null && !_providerProcess.HasExited)
                {
                    Console.WriteLine($"PresentMonProvider: Already monitoring PID {pid}");
                    return true;
                }

                _targetProcessId = (uint)pid;
                
                // Reset stale frame detection when switching PIDs
                _lastFrameCount = 0;
                _staleFrameCounter = 0;

                // Check if provider is already running (look for window)
                var hWnd = User32.FindWindow(null, CONNECT_WND_NAME);

                if (!hWnd.IsNull)
                {
                    // Provider is already running, just send it a message to switch to new PID
                    Console.WriteLine($"PresentMonProvider: Found existing instance, sending PID {pid}");
                    User32.PostMessage(hWnd, UM_SET_TARGET_PROCESS, (IntPtr)pid, IntPtr.Zero);
                }
                else
                {
                    // No existing instance, launch a new one
                    if (!File.Exists(_providerPath))
                    {
                        Console.WriteLine($"PresentMonProvider: ERROR - Provider not found at: {_providerPath}");
                        return false;
                    }

                    Console.WriteLine($"PresentMonProvider: Launching PresentMonDataProvider.exe with -i {pid}...");
                    _providerProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = _providerPath,
                            Arguments = $"-i {pid}", // Install flag + initial PID
                            WorkingDirectory = Path.GetDirectoryName(_providerPath),
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = false,
                            RedirectStandardError = false
                        }
                    };

                    _providerProcess.Start();
                    Console.WriteLine($"PresentMonProvider: Provider started (PID: {_providerProcess.Id})");

                    // Give provider time to initialize
                    await Task.Delay(2000);

                    // Check if provider is still running
                    if (_providerProcess.HasExited)
                    {
                        Console.WriteLine($"PresentMonProvider: ERROR - Provider exited immediately (code: {_providerProcess.ExitCode})");
                        return false;
                    }

                    Console.WriteLine("PresentMonProvider: Provider is running");
                }

                // Start polling loop if not already running
                if (_pollingTask == null || _pollingTask.IsCompleted)
                {
                    _pollingCts = new CancellationTokenSource();
                    _pollingTask = Task.Run(() => PollingLoopAsync(_pollingCts.Token));
                    Console.WriteLine("PresentMonProvider: Polling loop started");
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMonProvider: Error starting monitoring: {ex.Message}");
                return false;
            }
        }

        private async Task PollingLoopAsync(CancellationToken ct)
        {
            Console.WriteLine("PresentMonProvider: Polling loop running...");
            int retryCount = 0;
            const int maxRetries = 10;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // Check if provider is still running
                    if (_providerProcess == null || _providerProcess.HasExited)
                    {
                        Console.WriteLine("PresentMonProvider: Provider process exited, stopping polling");
                        break;
                    }

                    // Try to open shared memory if not already open
                    if (_pmdpSharedMemory == null && retryCount < maxRetries)
                    {
                        try
                        {
                            _pmdpSharedMemory = MemoryMappedFile.OpenExisting("PMDPSharedMemory");
                            Console.WriteLine("PresentMonProvider: Shared memory opened");
                            retryCount = 0; // Reset retry count on success
                        }
                        catch (FileNotFoundException)
                        {
                            retryCount++;
                            if (retryCount >= maxRetries)
                            {
                                Console.WriteLine($"PresentMonProvider: Failed to open shared memory after {maxRetries} attempts");
                            }
                            await Task.Delay(500, ct);
                            continue;
                        }
                    }

                    // Read FPS data from shared memory
                    if (_pmdpSharedMemory != null)
                    {
                        using (var accessor = _pmdpSharedMemory.CreateViewAccessor())
                        {
                            // Read header
                            uint signature = accessor.ReadUInt32(0);
                            uint version = accessor.ReadUInt32(4);
                            uint frameArrEntrySize = accessor.ReadUInt32(8);
                            uint frameArrOffset = accessor.ReadUInt32(12);
                            uint frameArrSize = accessor.ReadUInt32(16);
                            uint frameCount = accessor.ReadUInt32(20);
                            uint framePos = accessor.ReadUInt32(24);
                            uint status = accessor.ReadUInt32(28);

                            _frameCounter++;

                            // Debug output for header (always log first 10 frames, then every 60)
                            if (_frameCounter <= 10 || _frameCounter % 60 == 0)
                            {
                                Console.WriteLine($"PresentMonProvider: [Frame {_frameCounter}] Sig=0x{signature:X8}, Ver=0x{version:X8}, Status={status}");
                                Console.WriteLine($"PresentMonProvider: [Frame {_frameCounter}] EntrySize={frameArrEntrySize}, Offset={frameArrOffset}, Size={frameArrSize}");
                                Console.WriteLine($"PresentMonProvider: [Frame {_frameCounter}] FrameCount={frameCount}, FramePos={framePos}");
                            }

                            // Detect stale data - if FrameCount hasn't increased, data is stale
                            if (frameCount == _lastFrameCount)
                            {
                                _staleFrameCounter++;
                                
                                if (_staleFrameCounter == MAX_STALE_READS)
                                {
                                    Console.WriteLine($"PresentMonProvider: Data is stale (FrameCount stuck at {frameCount}). Waiting for new frames...");
                                }
                            }
                            else
                            {
                                // New frames detected - reset stale counter
                                if (_staleFrameCounter >= MAX_STALE_READS)
                                {
                                    Console.WriteLine($"PresentMonProvider: New frames detected! FrameCount: {_lastFrameCount} -> {frameCount}");
                                }
                                else if (_lastFrameCount > 0 && frameCount == 0)
                                {
                                    Console.WriteLine($"PresentMonProvider: Buffer reset detected! FrameCount: {_lastFrameCount} -> 0");
                                }
                                _staleFrameCounter = 0;
                                _lastFrameCount = frameCount;
                            }

                            // Verify signature
                            if (signature == 0x504D4450 && frameCount > 0 && _staleFrameCounter < MAX_STALE_READS) // 'PMDP' and not stale
                            {
                                // Read the most recent frame (at framePos - 1)
                                uint lastFrameIndex = (framePos > 0 ? framePos - 1 : frameArrSize - 1) % frameArrSize;
                                long frameOffset = frameArrOffset + (lastFrameIndex * frameArrEntrySize);

                                // Debug: read application name to verify offset
                                byte[] appNameBytes = new byte[260];
                                for (int i = 0; i < 260; i++)
                                {
                                    appNameBytes[i] = accessor.ReadByte(frameOffset + i);
                                }
                                string appName = System.Text.Encoding.ASCII.GetString(appNameBytes).TrimEnd('\0');
                                
                                if (_frameCounter % 60 == 0 && !string.IsNullOrWhiteSpace(appName))
                                {
                                    Console.WriteLine($"PresentMonProvider: Application in frame data: '{appName}'");
                                }

                                // PM_FRAME_DATA_V1 structure:
                                // char[260] + uint32 + uint64 + 6*uint32 + 11*double + 2*uint32 + uint64 = 260+4+8+24+88+8+8 = 400 bytes (with padding)
                                // Read some V1 fields to verify structure
                                uint processId = accessor.ReadUInt32(frameOffset + 260);
                                double msBetweenPresents = accessor.ReadDouble(frameOffset + 260 + 4 + 8 + 4 + 4 + 4 + 4); // Skip to msBetweenPresents
                                
                                if (_frameCounter % 60 == 0)
                                {
                                    Console.WriteLine($"PresentMonProvider: ProcessID={processId}, msBetweenPresents={msBetweenPresents:F2}ms");
                                }

                                // PM_FRAME_DATA_V2 starts after PM_FRAME_DATA_V1
                                // V1 size with proper alignment should be around 392-400 bytes
                                // Try reading at different offsets to find the correct one
                                long[] possibleV2Offsets = { frameOffset + 392, frameOffset + 396, frameOffset + 400 };
                                
                                foreach (var testOffset in possibleV2Offsets)
                                {
                                    double testFrameTime = accessor.ReadDouble(testOffset + 8); // CPUStart (8 bytes) + Frametime (double)
                                    
                                    // Frametime should be reasonable (1-1000ms for 1-1000 FPS)
                                    if (testFrameTime > 0 && testFrameTime < 1000)
                                    {
                                        long v2Offset = testOffset;
                                        
                                        // Read PM_FRAME_DATA_V2 fields
                                        ulong cpuStart = accessor.ReadUInt64(v2Offset);
                                        double frameTime = accessor.ReadDouble(v2Offset + 8);
                                        double cpuBusy = accessor.ReadDouble(v2Offset + 16);
                                        double cpuWait = accessor.ReadDouble(v2Offset + 24);
                                        double gpuLatency = accessor.ReadDouble(v2Offset + 32);
                                        double gpuTime = accessor.ReadDouble(v2Offset + 40);
                                        double gpuBusy = accessor.ReadDouble(v2Offset + 48);
                                        double videoBusy = accessor.ReadDouble(v2Offset + 56);
                                        double gpuWait = accessor.ReadDouble(v2Offset + 64);
                                        double displayLatency = accessor.ReadDouble(v2Offset + 72);

                                        // Calculate FPS from frame time (frameTime is in milliseconds)
                                        float fps = frameTime > 0 ? (float)(1000.0 / frameTime) : 0;

                                        if (fps > 0 && fps < 10000) // Sanity check
                                        {
                                            var frameData = new FrameData
                                            {
                                                Fps = fps,
                                                FrameTimeMs = (float)frameTime,
                                                GpuTimeMs = (float)gpuTime,
                                                GpuLatencyMs = (float)gpuLatency,
                                                DisplayLatencyMs = (float)displayLatency,
                                                CpuBusyMs = (float)cpuBusy,
                                                OnePercentLowFps = fps * 0.9f, // Placeholder - would need rolling window
                                                ZeroPointOnePercentLowFps = fps * 0.8f // Placeholder
                                            };

                                            MetricsUpdated?.Invoke(this, frameData);
                                            
                                            if (_frameCounter % 60 == 0)
                                            {
                                                Console.WriteLine($"PresentMonProvider: [V2 at offset {v2Offset - frameOffset}] FPS={fps:F1}, FrameTime={frameTime:F2}ms, GPU={gpuTime:F2}ms");
                                            }
                                        }
                                        
                                        break; // Found valid data, exit loop
                                    }
                                }
                            }
                            else if (signature != 0x504D4450)
                            {
                                if (_frameCounter % 300 == 0) // Log every 5 seconds
                                {
                                    Console.WriteLine($"PresentMonProvider: Invalid signature: 0x{signature:X8}, expected 0x504D4450");
                                }
                            }
                            else if (frameCount == 0)
                            {
                                if (_frameCounter % 300 == 0) // Log every 5 seconds
                                {
                                    Console.WriteLine($"PresentMonProvider: No frames captured yet (frameCount=0)");
                                }
                            }
                        }
                    }
                    else
                    {
                        if (_frameCounter % 300 == 0)
                        {
                            Console.WriteLine("PresentMonProvider: Shared memory is null in polling loop");
                        }
                    }

                    await Task.Delay(16, ct); // ~60 Hz polling
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"PresentMonProvider: Polling error: {ex.Message}");
                    await Task.Delay(1000, ct);
                }
            }

            Console.WriteLine("PresentMonProvider: Polling loop stopped");
        }

        public async Task StopMonitoringAsync()
        {
            Console.WriteLine("PresentMonProvider: Stopping monitoring...");

            // Stop polling loop
            _pollingCts?.Cancel();
            if (_pollingTask != null)
            {
                await _pollingTask;
                _pollingTask = null;
            }

            // Close shared memory
            _pmdpSharedMemory?.Dispose();
            _pmdpSharedMemory = null;

            // Kill provider process
            if (_providerProcess != null && !_providerProcess.HasExited)
            {
                try
                {
                    _providerProcess.Kill();
                    _providerProcess.WaitForExit(2000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PresentMonProvider: Error killing provider: {ex.Message}");
                }
            }
            _providerProcess?.Dispose();
            _providerProcess = null;

            _targetProcessId = 0;
            Console.WriteLine("PresentMonProvider: Stopped");
        }

        public void Dispose()
        {
            StopMonitoringAsync().Wait();
        }
    }
}
