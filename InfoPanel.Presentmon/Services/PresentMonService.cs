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
        private readonly List<float> _frameTimes = new();
        private const int MaxFrameSamples = 1000;
        private CancellationTokenSource? _processCts;
        private string? _currentSessionName;

        public async Task<bool> OpenSessionAsync(uint processId, string processName)
        {
            try
            {
                var exePath = GetDataProviderPath();
                if (!File.Exists(exePath))
                {
                    Console.WriteLine($"PresentMon executable not found at: {exePath}");
                    return false;
                }

                var providerDirectory = Path.GetDirectoryName(exePath)!;

                await TerminateExistingSessionAsync(exePath, providerDirectory);

                _currentSessionName = $"InfoPanel_{Guid.NewGuid():N}";
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
                if (_presentMonProcess == null) return false;

                _frameTimes.Clear();
                _processCts = new CancellationTokenSource();
                
                _ = Task.Run(() => ProcessOutputAsync(_processCts.Token));

                Console.WriteLine($"Started PresentMon monitoring for {processName} (PID: {processId})");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to start PresentMon: {ex.Message}");
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
                    await _presentMonProcess.WaitForExitAsync();
                }

                _presentMonProcess?.Dispose();
                _presentMonProcess = null;
                _frameTimes.Clear();

                var exePath = GetDataProviderPath();
                if (File.Exists(exePath))
                {
                    var providerDirectory = Path.GetDirectoryName(exePath)!;
                    await TerminateExistingSessionAsync(exePath, providerDirectory);
                }

                Console.WriteLine("PresentMon monitoring stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping PresentMon: {ex.Message}");
            }
        }

        private async Task ProcessOutputAsync(CancellationToken cancellationToken)
        {
            if (_presentMonProcess?.StandardOutput == null) return;

            try
            {
                string? line;
                while ((line = await _presentMonProcess.StandardOutput.ReadLineAsync()) != null 
                       && !cancellationToken.IsCancellationRequested)
                {
                    ProcessOutputLine(line);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing PresentMon output: {ex.Message}");
            }
        }

        private void ProcessOutputLine(string line)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Application,ProcessID"))
                    return;

                var columns = line.Split(',');
                if (columns.Length <= 9) return;

                if (float.TryParse(columns[9], NumberStyles.Float, CultureInfo.InvariantCulture, out float frameTimeMs))
                {
                    if (frameTimeMs > 0 && frameTimeMs < 1000)
                    {
                        UpdateMetrics(frameTimeMs);
                    }
                }
            }
            catch { }
        }

        private void UpdateMetrics(float frameTimeMs)
        {
            _frameTimes.Add(frameTimeMs);
            if (_frameTimes.Count > MaxFrameSamples)
            {
                _frameTimes.RemoveAt(0);
            }

            if (_frameTimes.Count == 0) return;

            float avgFrameTime = _frameTimes.Sum() / _frameTimes.Count;
            float fps = 1000f / avgFrameTime;

            var sortedFrameTimes = _frameTimes.OrderByDescending(x => x).ToList();
            int onePercentIndex = Math.Max(0, (int)(_frameTimes.Count * 0.01) - 1);
            float onePercentLowFrameTime = sortedFrameTimes.ElementAtOrDefault(onePercentIndex);
            float onePercentLowFps = onePercentLowFrameTime > 0 ? 1000f / onePercentLowFrameTime : 0;

            var frameData = new FrameData
            {
                Fps = fps,
                FrameTimeMs = avgFrameTime,
                OnePercentLowFps = onePercentLowFps
            };

            MetricsUpdated?.Invoke(this, frameData);
        }

        private string GetDataProviderPath()
        {
            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return Path.Combine(assemblyDir!, "PresentMonDataProvider", "PresentMonDataProvider.exe");
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
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to terminate existing PresentMon session: {ex.Message}");
            }
        }

        public void Dispose()
        {
            StopMonitoringAsync().Wait(5000);
            _processCts?.Dispose();
        }
    }
}
