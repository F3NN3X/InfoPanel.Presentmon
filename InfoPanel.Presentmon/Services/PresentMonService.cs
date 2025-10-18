using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.ServiceProcess;
using InfoPanel.Presentmon.Models;

namespace InfoPanel.Presentmon.Services
{
    public class PresentMonService : IDisposable
    {
    public event EventHandler<FrameData>? MetricsUpdated;

    private readonly PresentMonBridgeClient _bridgeClient;
    private bool _bridgeActive;
    private bool _bridgeServiceEnsured;
    private readonly SemaphoreSlim _bridgeServiceLock = new(1, 1);
    private const string BridgeServiceName = "PresentMonBridgeService";

    private Process? _presentMonProcess;
    private CancellationTokenSource? _processCts;

        private readonly List<float> _frameTimes = new();
        private const int MaxFrameSamples = 1000;
        private const int DiagnosticLogLimit = 10;
        private const int RawLineLogLimit = 10;
        private const int MetricLogLimit = 5;

        private Dictionary<string, int>? _columnIndexMap;
        private int _maxColumnIndex;
        private int _diagnosticLinesLogged;
        private int _metricLinesLogged;
        private bool _headerLogged;
        private int _rawLinesLogged;
    private float _lastGpuLatency;
    private float _lastGpuTime;
    private float _lastGpuBusy;
    private float _lastGpuWait;
    private float _lastDisplayLatency;
    private float _lastCpuBusy;
    private float _lastCpuWait;
    private float _lastGpuUtilization;

        public PresentMonService()
        {
            _bridgeClient = new PresentMonBridgeClient();
            _bridgeClient.MetricsReceived += OnBridgeMetricsReceived;
            _bridgeClient.Disconnected += OnBridgeDisconnected;
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

        private static readonly string[] FpsColumnCandidates =
        {
            "DisplayedFPS",
            "AverageFPS",
            "FPS",
            "Framerate"
        };

        private static readonly string[] DroppedColumnCandidates =
        {
            "Dropped",
            "WasDropped",
            "DroppedByDisplay"
        };

        public async Task<bool> OpenSessionAsync(uint processId, string processName)
        {
            try
            {
                bool bridgeStarted = await TryStartBridgeSessionAsync(processId, processName).ConfigureAwait(false);
                if (bridgeStarted)
                {
                    Console.WriteLine("PresentMon: capturing via bridge service.");
                    return true;
                }

                Console.WriteLine("PresentMon: bridge start failed. No legacy PresentMon fallback is available.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: failed to start bridge session. {ex.Message}");
                return false;
            }
        }

        public async Task StopMonitoringAsync()
        {
            try
            {
                Console.WriteLine("PresentMon: stop requested.");

                if (_bridgeActive)
                {
                    Console.WriteLine("PresentMon: stopping bridge session.");
                    try
                    {
                        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _bridgeClient.StopMonitoringAsync(stopCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon: bridge stop failed. {ex.Message}");
                    }
                    finally
                    {
                        _bridgeActive = false;
                        _bridgeClient.Disconnect();
                    }
                }

                _frameTimes.Clear();
                ResetParsingState();

                Console.WriteLine("PresentMon: bridge session stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error during stop. {ex.Message}");
            }
        }

        private void OnBridgeMetricsReceived(object? sender, FrameData frameData)
        {
            MetricsUpdated?.Invoke(this, frameData);
        }

        private void OnBridgeDisconnected(object? sender, EventArgs e)
        {
            if (_bridgeActive)
            {
                _bridgeActive = false;
                Console.WriteLine("PresentMon: bridge connection lost. Next session will attempt to restart the bridge.");
            }
        }

        private async Task StopBridgeServiceAsync()
        {
            await _bridgeServiceLock.WaitAsync().ConfigureAwait(false);
            try
            {
                ServiceController? controller = null;
                try
                {
                    controller = new ServiceController(BridgeServiceName);
                    controller.Refresh();
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PresentMon: failed to access bridge service. {ex.Message}");
                    return;
                }

                using (controller)
                {
                    Console.WriteLine("PresentMon: stopping bridge service.");
                    try
                    {
                        if (controller.Status != ServiceControllerStatus.Stopped && controller.Status != ServiceControllerStatus.StopPending)
                        {
                            controller.Stop();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon: direct stop failed ({ex.Message}). Falling back to sc.exe.");
                        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await RunScCommandAsync($"stop {BridgeServiceName}", 15000, stopCts.Token, ignoreErrors: true).ConfigureAwait(false);
                    }

                    try
                    {
                        controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon: waiting for bridge service shutdown failed. {ex.Message}");
                    }
                }

                _bridgeServiceEnsured = false;
            }
            finally
            {
                _bridgeServiceLock.Release();
            }
        }

        private async Task ProcessOutputAsync(CancellationToken cancellationToken)
        {
            if (_presentMonProcess?.StandardOutput == null)
            {
                return;
            }

            try
            {
                Console.WriteLine("PresentMon: stdout reader started.");
                string? line;
                while ((line = await _presentMonProcess.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null &&
                       !cancellationToken.IsCancellationRequested)
                {
                    ProcessOutputLine(line);
                }

                Console.WriteLine("PresentMon: stdout reader completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error reading stdout. {ex.Message}");
            }
        }

        private async Task ProcessErrorOutputAsync(CancellationToken cancellationToken)
        {
            if (_presentMonProcess?.StandardError == null)
            {
                return;
            }

            try
            {
                Console.WriteLine("PresentMon: stderr reader started.");
                string? line;
                while ((line = await _presentMonProcess.StandardError.ReadLineAsync().ConfigureAwait(false)) != null &&
                       !cancellationToken.IsCancellationRequested)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Console.WriteLine($"PresentMon stderr: {line}");
                    }
                }

                Console.WriteLine("PresentMon: stderr reader completed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error reading stderr. {ex.Message}");
            }
        }

        private async Task<bool> TryStartBridgeSessionAsync(uint processId, string processName)
        {
            try
            {
                using var ensureCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (!await EnsureBridgeServiceRunningAsync(ensureCts.Token).ConfigureAwait(false))
                {
                    Console.WriteLine("PresentMon: unable to start bridge service.");
                    return false;
                }

                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                if (!await _bridgeClient.ConnectAsync(connectCts.Token).ConfigureAwait(false))
                {
                    Console.WriteLine("PresentMon: bridge service unavailable.");
                    return false;
                }

                using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (await _bridgeClient.StartMonitoringAsync(processId, processName, startCts.Token).ConfigureAwait(false))
                {
                    _bridgeActive = true;
                    return true;
                }

                Console.WriteLine("PresentMon: bridge rejected start request.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: bridge session failed ({ex.Message}).");
            }

            _bridgeClient.Disconnect(notify: false);
            _bridgeActive = false;
            return false;
        }

        private async Task<bool> EnsureBridgeServiceRunningAsync(CancellationToken cancellationToken)
        {
            await _bridgeServiceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_bridgeServiceEnsured)
                {
                    try
                    {
                        using var existingController = new ServiceController(BridgeServiceName);
                        existingController.Refresh();
                        if (existingController.Status == ServiceControllerStatus.Running)
                        {
                            return true;
                        }

                        Console.WriteLine("PresentMon: bridge service not running, attempting restart.");
                        _bridgeServiceEnsured = false;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMon: unable to query bridge service status. {ex.Message}");
                        _bridgeServiceEnsured = false;
                    }
                }

                var servicePath = GetBridgeServiceExecutablePath();
                if (string.IsNullOrEmpty(servicePath) || !File.Exists(servicePath))
                {
                    Console.WriteLine("PresentMon: bridge service executable not found. Expected at:");
                    Console.WriteLine("  " + (servicePath ?? "(unknown path)"));
                    return false;
                }

                bool serviceExists = false;
                try
                {
                    serviceExists = ServiceController.GetServices()
                        .Any(s => s.ServiceName.Equals(BridgeServiceName, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PresentMon: failed to enumerate services. {ex.Message}");
                }

                if (!serviceExists)
                {
                    Console.WriteLine($"PresentMon: installing {BridgeServiceName}.");
                    if (!await RunScCommandAsync($"create {BridgeServiceName} binPath= \"{servicePath}\" start= demand", 15000, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                }
                else
                {
                    await RunScCommandAsync($"stop {BridgeServiceName}", 15000, cancellationToken, ignoreErrors: true).ConfigureAwait(false);
                    await RunScCommandAsync($"config {BridgeServiceName} binPath= \"{servicePath}\" start= demand", 15000, cancellationToken, ignoreErrors: true).ConfigureAwait(false);
                }

                await RunScCommandAsync($"description {BridgeServiceName} \"InfoPanel PresentMon bridge (LocalSystem)\"", 5000, cancellationToken, ignoreErrors: true).ConfigureAwait(false);

                try
                {
                    using var controller = new ServiceController(BridgeServiceName);
                    controller.Refresh();
                    if (controller.Status != ServiceControllerStatus.Running)
                    {
                        Console.WriteLine($"PresentMon: starting {BridgeServiceName}.");
                        await RunScCommandAsync($"start {BridgeServiceName}", 15000, cancellationToken, ignoreErrors: true).ConfigureAwait(false);

                        controller.Refresh();
                        controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"PresentMon: failed while starting {BridgeServiceName}. {ex.Message}");
                    return false;
                }

                _bridgeServiceEnsured = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error ensuring bridge service. {ex.Message}");
                return false;
            }
            finally
            {
                _bridgeServiceLock.Release();
            }
        }

        private static string? GetBridgeServiceExecutablePath()
        {
            try
            {
                var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(assemblyDir))
                {
                    return null;
                }

                return Path.Combine(assemblyDir, "PresentMonBridgeService", "PresentMon.BridgeService.exe");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: failed to resolve bridge service path. {ex.Message}");
                return null;
            }
        }

        private static async Task<bool> RunScCommandAsync(string arguments, int timeoutMilliseconds, CancellationToken cancellationToken, bool ignoreErrors = false)
        {
            Console.WriteLine($"PresentMon bridge: sc.exe {arguments}");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            try
            {
                if (!process.Start())
                {
                    Console.WriteLine("PresentMon: failed to start sc.exe process.");
                    return false;
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMilliseconds));

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"PresentMon: sc.exe {arguments} timed out.");
                    try
                    {
                        process.Kill(true);
                    }
                    catch
                    {
                    }

                    return false;
                }

                var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
                var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(output))
                {
                    Console.WriteLine($"PresentMon bridge (sc): {output.Trim()}");
                }

                if (!string.IsNullOrWhiteSpace(error))
                {
                    Console.WriteLine($"PresentMon bridge (sc err): {error.Trim()}");
                }

                if (process.ExitCode != 0 && !ignoreErrors)
                {
                    Console.WriteLine($"PresentMon: sc.exe exited with code {process.ExitCode} for '{arguments}'.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                if (!ignoreErrors)
                {
                    Console.WriteLine($"PresentMon: sc.exe command '{arguments}' failed. {ex.Message}");
                }

                return false;
            }
        }

        private void ProcessOutputLine(string line)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                if (!_headerLogged && _rawLinesLogged < RawLineLogLimit)
                {
                    Console.WriteLine($"PresentMon raw: {line}");
                    _rawLinesLogged++;
                }

                if (line.StartsWith("warning", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"PresentMon message: {line}");
                    return;
                }

                var columns = SplitCsvLine(line);
                if (columns.Count == 0)
                {
                    return;
                }

                if (_columnIndexMap == null ||
                    string.Equals(columns[0], "Application", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseHeader(columns) && !_headerLogged)
                    {
                        Console.WriteLine($"PresentMon columns detected ({_columnIndexMap!.Count}): {string.Join(", ", _columnIndexMap.Keys)}");
                        _headerLogged = true;
                    }
                    return;
                }

                if (_columnIndexMap == null)
                {
                    LogDiagnostic($"PresentMon data skipped (header missing): {line}");
                    return;
                }

                if (columns.Count <= _maxColumnIndex)
                {
                    LogDiagnostic($"PresentMon data skipped (insufficient columns): {line}");
                    return;
                }

                if (ShouldSkipFrame(columns))
                {
                    return;
                }

                if (!TryGetFrameTime(columns, out float frameTimeMs))
                {
                    LogDiagnostic($"PresentMon data missing frame time: {line}");
                    return;
                }

                float? gpuLatencyMs = TryGetFloat(columns, "MsGPULatency", out float gpuLatency) ? gpuLatency : null;
                float? gpuTimeMs = TryGetFloat(columns, "MsGPUTime", out float gpuTime) ? gpuTime : null;
                float? gpuBusyMs = TryGetFloat(columns, "MsGPUBusy", out float gpuBusy) ? gpuBusy : null;
                float? gpuWaitMs = TryGetFloat(columns, "MsGPUWait", out float gpuWait) ? gpuWait : null;
                float? displayLatencyMs = TryGetFloat(columns, "MsUntilDisplayed", out float displayLatency) ? displayLatency : null;
                float? cpuBusyMs = TryGetFloat(columns, "MsCPUBusy", out float cpuBusy) ? cpuBusy : null;
                float? cpuWaitMs = TryGetFloat(columns, "MsCPUWait", out float cpuWait) ? cpuWait : null;

                if (frameTimeMs > 0 && frameTimeMs < 1000)
                {
                    UpdateMetrics(frameTimeMs, gpuLatencyMs, gpuTimeMs, gpuBusyMs, gpuWaitMs, displayLatencyMs, cpuBusyMs, cpuWaitMs);
                }
                else
                {
                    LogDiagnostic($"PresentMon frame time outside expected range: {frameTimeMs:F3} ms");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: failed to process line. {ex.Message}");
            }
        }

        private void UpdateMetrics(
            float frameTimeMs,
            float? gpuLatencyMs,
            float? gpuTimeMs,
            float? gpuBusyMs,
            float? gpuWaitMs,
            float? displayLatencyMs,
            float? cpuBusyMs,
            float? cpuWaitMs)
        {
            _frameTimes.Add(frameTimeMs);
            if (_frameTimes.Count > MaxFrameSamples)
            {
                _frameTimes.RemoveAt(0);
            }

            if (_frameTimes.Count == 0)
            {
                return;
            }

            if (gpuLatencyMs.HasValue)
            {
                _lastGpuLatency = gpuLatencyMs.Value;
            }

            if (gpuTimeMs.HasValue)
            {
                _lastGpuTime = gpuTimeMs.Value;
            }

            if (gpuBusyMs.HasValue)
            {
                _lastGpuBusy = gpuBusyMs.Value;

                if (frameTimeMs > 0.0001f)
                {
                    float utilization = (_lastGpuBusy / frameTimeMs) * 100f;
                    _lastGpuUtilization = Math.Clamp(utilization, 0f, 100f);
                }
            }

            if (gpuWaitMs.HasValue)
            {
                _lastGpuWait = gpuWaitMs.Value;
            }

            if (displayLatencyMs.HasValue)
            {
                _lastDisplayLatency = displayLatencyMs.Value;
            }

            if (cpuBusyMs.HasValue)
            {
                _lastCpuBusy = cpuBusyMs.Value;
            }

            if (cpuWaitMs.HasValue)
            {
                _lastCpuWait = cpuWaitMs.Value;
            }

            float avgFrameTime = _frameTimes.Sum() / _frameTimes.Count;
            float fps = avgFrameTime > 0 ? 1000f / avgFrameTime : 0f;

            var sortedFrameTimes = _frameTimes.OrderByDescending(x => x).ToList();
            int onePercentIndex = Math.Max(0, (int)Math.Ceiling(_frameTimes.Count * 0.01f) - 1);
            float onePercentLowFrameTime = sortedFrameTimes.ElementAtOrDefault(onePercentIndex);
            float onePercentLowFps = onePercentLowFrameTime > 0 ? 1000f / onePercentLowFrameTime : 0;

            int zeroPointOnePercentIndex = Math.Max(0, (int)Math.Ceiling(_frameTimes.Count * 0.001f) - 1);
            float zeroPointOneLowFrameTime = sortedFrameTimes.ElementAtOrDefault(zeroPointOnePercentIndex);
            float zeroPointOnePercentLowFps = zeroPointOneLowFrameTime > 0 ? 1000f / zeroPointOneLowFrameTime : 0;

            if (_metricLinesLogged < MetricLogLimit)
            {
                Console.WriteLine($"PresentMon metrics: samples={_frameTimes.Count}, avg={avgFrameTime:F3}ms, fps={fps:F1}, 1pctLow={onePercentLowFps:F1}, 0.1pctLow={zeroPointOnePercentLowFps:F1}");
                _metricLinesLogged++;
            }

            MetricsUpdated?.Invoke(this, new FrameData
            {
                Fps = fps,
                FrameTimeMs = avgFrameTime,
                OnePercentLowFps = onePercentLowFps,
                ZeroPointOnePercentLowFps = zeroPointOnePercentLowFps,
                GpuLatencyMs = _lastGpuLatency,
                GpuTimeMs = _lastGpuTime,
                GpuBusyMs = _lastGpuBusy,
                GpuWaitMs = _lastGpuWait,
                DisplayLatencyMs = _lastDisplayLatency,
                CpuBusyMs = _lastCpuBusy,
                CpuWaitMs = _lastCpuWait,
                GpuUtilizationPercent = _lastGpuUtilization,
                Timestamp = DateTime.Now
            });
        }

        private string? ResolvePresentMonExecutable(out bool isConsoleBuild)
        {
            isConsoleBuild = false;

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (assemblyDir == null)
            {
                return null;
            }

            var providerDir = Path.Combine(assemblyDir, "PresentMonDataProvider");
            if (Directory.Exists(providerDir))
            {
                string[] consoleCandidates =
                {
                    "PresentMon-2.3.1-x64.exe",
                    "PresentMon-2.3.1-x64-DLSS4.exe",
                    "PresentMon-1.10.0-x64.exe"
                };

                foreach (var candidate in consoleCandidates)
                {
                    var candidatePath = Path.Combine(providerDir, candidate);
                    if (File.Exists(candidatePath))
                    {
                        isConsoleBuild = true;
                        return candidatePath;
                    }
                }

                var providerExe = Path.Combine(providerDir, "PresentMonDataProvider.exe");
                if (File.Exists(providerExe))
                {
                    return providerExe;
                }
            }

            return null;
        }

        private static string BuildLaunchArguments(uint processId, string processName, bool useConsoleBuild)
        {
            if (useConsoleBuild)
            {
                var targetName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? processName
                    : processName + ".exe";

                var arguments = new[]
                {
                    $"--process_name \"{targetName}\"",
                    "--output_stdout",
                    "--stop_existing_session",
                    "--terminate_on_proc_exit",
                    "--no_console_stats",
                    "--set_circular_buffer_size 8192",
                    "--qpc_time",
                    "--session_name PresentMonDataProvider"
                };

                return string.Join(' ', arguments);
            }

            var providerArgs = new[]
            {
                $"-process_id={processId}",
                "-output_stdout",
                "-stop_existing_session",
                "-terminate_on_proc_exit",
                "-qpc_time"
            };

            return string.Join(' ', providerArgs);
        }

        private static string BuildTerminateArguments(bool useConsoleBuild)
        {
            if (useConsoleBuild)
            {
                return "--terminate_existing_session --session_name PresentMonDataProvider";
            }

            return "-terminate_existing_session";
        }

        private void ResetParsingState()
        {
            _columnIndexMap = null;
            _maxColumnIndex = 0;
            _diagnosticLinesLogged = 0;
            _headerLogged = false;
            _rawLinesLogged = 0;
            _lastGpuLatency = 0f;
            _lastGpuTime = 0f;
            _lastGpuBusy = 0f;
            _lastGpuWait = 0f;
            _lastDisplayLatency = 0f;
            _lastCpuBusy = 0f;
            _lastCpuWait = 0f;
            _lastGpuUtilization = 0f;
        }

        private bool TryParseHeader(IReadOnlyList<string> columns)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < columns.Count; i++)
            {
                var name = columns[i].Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!map.ContainsKey(name))
                {
                    map[name] = i;
                }
            }

            if (map.Count == 0)
            {
                return false;
            }

            _columnIndexMap = map;
            _maxColumnIndex = map.Values.Max();
            _diagnosticLinesLogged = 0;
            _metricLinesLogged = 0;
            return true;
        }

        private bool ShouldSkipFrame(IReadOnlyList<string> columns)
        {
            foreach (var columnName in DroppedColumnCandidates)
            {
                if (TryGetInt(columns, columnName, out int dropped) && dropped != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetFrameTime(IReadOnlyList<string> columns, out float frameTimeMs)
        {
            foreach (var column in FrameTimeColumnCandidates)
            {
                if (TryGetFloat(columns, column, out frameTimeMs))
                {
                    return true;
                }
            }

            foreach (var column in FpsColumnCandidates)
            {
                if (TryGetFloat(columns, column, out float fps) && fps > 0)
                {
                    frameTimeMs = 1000f / fps;
                    return true;
                }
            }

            frameTimeMs = 0f;
            return false;
        }

        private bool TryGetFloat(IReadOnlyList<string> columns, string columnName, out float value)
        {
            value = 0f;

            if (_columnIndexMap == null || !_columnIndexMap.TryGetValue(columnName, out int index))
            {
                return false;
            }

            if (index < 0 || index >= columns.Count)
            {
                return false;
            }

            var raw = columns[index].Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                return true;
            }

            return false;
        }

        private bool TryGetInt(IReadOnlyList<string> columns, string columnName, out int value)
        {
            value = 0;

            if (_columnIndexMap == null || !_columnIndexMap.TryGetValue(columnName, out int index))
            {
                return false;
            }

            if (index < 0 || index >= columns.Count)
            {
                return false;
            }

            var raw = columns[index].Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            {
                return true;
            }

            return false;
        }

        private void LogDiagnostic(string message)
        {
            if (_diagnosticLinesLogged >= DiagnosticLogLimit)
            {
                return;
            }

            Console.WriteLine(message);
            _diagnosticLinesLogged++;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null)
            {
                return result;
            }

            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            result.Add(current.ToString());

            for (int i = 0; i < result.Count; i++)
            {
                result[i] = result[i].Trim();
            }

            return result;
        }

        private async Task TerminateExistingSessionAsync(string exePath, string workingDirectory, bool useConsoleBuild)
        {
            try
            {
                Console.WriteLine("PresentMon: invoking terminate_existing_session helper.");
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = BuildTerminateArguments(useConsoleBuild),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync().ConfigureAwait(false);
                    Console.WriteLine($"PresentMon: terminate_existing_session exit code {process.ExitCode}.");
                }
                else
                {
                    Console.WriteLine("PresentMon: terminate_existing_session launch failed.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: failed to terminate existing session. {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                StopMonitoringAsync().Wait(2000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error stopping monitoring during dispose. {ex.Message}");
            }

            try
            {
                StopBridgeServiceAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error stopping bridge service during dispose. {ex.Message}");
            }

            _processCts?.Dispose();
            _bridgeClient.MetricsReceived -= OnBridgeMetricsReceived;
            _bridgeClient.Disconnected -= OnBridgeDisconnected;
            _bridgeClient.Dispose();
            _bridgeServiceLock.Dispose();
        }
    }
}
