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
        private const int DiagnosticLogLimit = 5;

        private Dictionary<string, int>? _columnIndexMap;
        private int _maxColumnIndex;
        private int _diagnosticLinesLogged;
        private bool _headerLogged;

        private static readonly string[] FrameTimeColumnCandidates =
        {
            "MsBetweenPresents",
            "MsBetweenDisplays",
            "MsUntilDisplayed",
            "MsInPresentAPI",
            "FrameTimeMs",
            "frame_time_ms",
            "FrameTime",
            "frame_time"
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
                var exePath = GetDataProviderPath();
                if (!File.Exists(exePath))
                {
                    Console.WriteLine($"PresentMon provider not found at: {exePath}");
                    return false;
                }

                var providerDirectory = Path.GetDirectoryName(exePath)!;
                await TerminateExistingSessionAsync(exePath, providerDirectory).ConfigureAwait(false);

                var arguments = string.Join(' ', new[]
                {
                    $"-process_id={processId}",
                    "-output_stdout",
                    "-stop_existing_session",
                    "-terminate_on_proc_exit",
                    "-qpc_time"
                });

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

                _presentMonProcess = Process.Start(startInfo);
                if (_presentMonProcess == null)
                {
                    Console.WriteLine("PresentMon: failed to launch provider process.");
                    return false;
                }

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
                _processCts?.Cancel();

                if (_presentMonProcess != null && !_presentMonProcess.HasExited)
                {
                    _presentMonProcess.Kill();
                    await _presentMonProcess.WaitForExitAsync().ConfigureAwait(false);
                }

                _presentMonProcess?.Dispose();
                _presentMonProcess = null;

                var exePath = GetDataProviderPath();
                if (File.Exists(exePath))
                {
                    var providerDirectory = Path.GetDirectoryName(exePath)!;
                    await TerminateExistingSessionAsync(exePath, providerDirectory).ConfigureAwait(false);
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
                string? line;
                while ((line = await _presentMonProcess.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null &&
                       !cancellationToken.IsCancellationRequested)
                {
                    ProcessOutputLine(line);
                }
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
                string? line;
                while ((line = await _presentMonProcess.StandardError.ReadLineAsync().ConfigureAwait(false)) != null &&
                       !cancellationToken.IsCancellationRequested)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        Console.WriteLine($"PresentMon stderr: {line}");
                    }
                }
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
                        Console.WriteLine($"PresentMon columns: {string.Join(", ", _columnIndexMap!.Keys)}");
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

                if (frameTimeMs > 0 && frameTimeMs < 1000)
                {
                    UpdateMetrics(frameTimeMs);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMon: failed to process line. {ex.Message}");
            }
        }

        private void UpdateMetrics(float frameTimeMs)
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

            float avgFrameTime = _frameTimes.Sum() / _frameTimes.Count;
            float fps = avgFrameTime > 0 ? 1000f / avgFrameTime : 0f;

            var sortedFrameTimes = _frameTimes.OrderByDescending(x => x).ToList();
            int onePercentIndex = Math.Max(0, (int)(_frameTimes.Count * 0.01) - 1);
            float onePercentLowFrameTime = sortedFrameTimes.ElementAtOrDefault(onePercentIndex);
            float onePercentLowFps = onePercentLowFrameTime > 0 ? 1000f / onePercentLowFrameTime : 0;

            MetricsUpdated?.Invoke(this, new FrameData
            {
                Fps = fps,
                FrameTimeMs = avgFrameTime,
                OnePercentLowFps = onePercentLowFps
            });
        }

        private string GetDataProviderPath()
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyDir!, "PresentMonDataProvider", "PresentMonDataProvider.exe");
        }

        private void ResetParsingState()
        {
            _columnIndexMap = null;
            _maxColumnIndex = 0;
            _diagnosticLinesLogged = 0;
            _headerLogged = false;
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

        private async Task TerminateExistingSessionAsync(string exePath, string workingDirectory)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "-terminate_existing_session",
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
