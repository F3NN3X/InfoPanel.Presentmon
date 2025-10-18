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
using InfoPanel.Presentmon.Models;

namespace InfoPanel.Presentmon.Services
{
    public class PresentMonService : IDisposable
    {
        public event EventHandler<FrameData>? MetricsUpdated;

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
                var exePath = ResolvePresentMonExecutable(out bool useConsoleBuild);
                if (string.IsNullOrEmpty(exePath))
                {
                    Console.WriteLine("PresentMon: no executable found in PresentMonDataProvider directory.");
                    return false;
                }

                var providerDirectory = Path.GetDirectoryName(exePath)!;
                Console.WriteLine($"PresentMon: using {(useConsoleBuild ? "console" : "provider")} executable at {exePath}");
                Console.WriteLine("PresentMon: terminating pre-existing session (if any).");
                await TerminateExistingSessionAsync(exePath, providerDirectory, useConsoleBuild).ConfigureAwait(false);

                var arguments = BuildLaunchArguments(processId, processName, useConsoleBuild);

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    WorkingDirectory = providerDirectory
                };

                Console.WriteLine($"PresentMon: launching provider '{startInfo.FileName}' with args: {startInfo.Arguments}");

                _presentMonProcess = Process.Start(startInfo);
                if (_presentMonProcess == null)
                {
                    Console.WriteLine("PresentMon: failed to launch provider process.");
                    return false;
                }

                _presentMonProcess.EnableRaisingEvents = true;
                _presentMonProcess.Exited += (sender, _) =>
                {
                    if (sender is Process proc)
                    {
                        Console.WriteLine($"PresentMon: provider exited (code: {proc.ExitCode})");
                    }
                    else
                    {
                        Console.WriteLine("PresentMon: provider exited.");
                    }
                };

                _frameTimes.Clear();
                ResetParsingState();

                _processCts = new CancellationTokenSource();
                _ = Task.Run(() => ProcessOutputAsync(_processCts.Token));
                _ = Task.Run(() => ProcessErrorOutputAsync(_processCts.Token));

                Console.WriteLine($"PresentMon: provider started for {processName} (PID {processId}).");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: failed to start provider. {ex.Message}");
                return false;
            }
        }

        public async Task StopMonitoringAsync()
        {
            try
            {
                Console.WriteLine("PresentMon: stop requested.");
                _processCts?.Cancel();

                if (_presentMonProcess != null)
                {
                    Console.WriteLine($"PresentMon: request stop (running={!_presentMonProcess.HasExited}, pid={_presentMonProcess.Id})");
                }

                if (_presentMonProcess != null && !_presentMonProcess.HasExited)
                {
                    _presentMonProcess.Kill();
                    await _presentMonProcess.WaitForExitAsync().ConfigureAwait(false);
                }

                if (_presentMonProcess != null)
                {
                    Console.WriteLine($"PresentMon: disposing provider process (exitCode={_presentMonProcess.ExitCode})");
                }

                _presentMonProcess?.Dispose();
                _presentMonProcess = null;

                var exePath = ResolvePresentMonExecutable(out bool useConsoleBuild);
                if (!string.IsNullOrEmpty(exePath))
                {
                    var providerDirectory = Path.GetDirectoryName(exePath)!;
                    await TerminateExistingSessionAsync(exePath, providerDirectory, useConsoleBuild).ConfigureAwait(false);
                }

                _frameTimes.Clear();
                ResetParsingState();

                Console.WriteLine("PresentMon: provider stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: error during stop. {ex.Message}");
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
            _metricLinesLogged = 0;
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
            StopMonitoringAsync().Wait(2000);
            _processCts?.Dispose();
        }
    }
}
