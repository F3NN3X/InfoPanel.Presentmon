using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Presentmon.Models;

namespace InfoPanel.Presentmon.Services
{
    public class PresentMonService : IDisposable
    {
        private readonly FrameProcessingConfig _config;
        private readonly string? _presentMonExecutablePath;
        private Process? _presentMonProcess;
        private bool _disposed = false;

        public event EventHandler<FrameMetrics>? MetricsUpdated;

        public PresentMonService(FrameProcessingConfig config)
        {
            _config = config;
            
            // Find PresentMon executable
            string? pluginDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(pluginDirectory))
            {
                string presentMonPath = Path.Combine(pluginDirectory, "PresentMonDataProvider", "PresentMon-2.3.1-x64-DLSS4.exe");
                if (File.Exists(presentMonPath))
                {
                    _presentMonExecutablePath = presentMonPath;
                    Console.WriteLine($"Found PresentMon executable: {presentMonPath}");
                }
            }
        }

        public async Task<bool> OpenSessionAsync(uint processId, string processName)
        {
            if (string.IsNullOrEmpty(_presentMonExecutablePath))
                return false;

            Console.WriteLine($"Starting PresentMon for PID: {processId}");
            
            // Create PresentMon process with enhanced debugging
            _presentMonProcess = new Process();
            _presentMonProcess.StartInfo.FileName = _presentMonExecutablePath;
            _presentMonProcess.StartInfo.Arguments = $"--process_id {processId} --output_stdout --v2_metrics --terminate_on_proc_exit";
            _presentMonProcess.StartInfo.UseShellExecute = false;
            _presentMonProcess.StartInfo.RedirectStandardOutput = true;
            _presentMonProcess.StartInfo.RedirectStandardError = true;
            _presentMonProcess.StartInfo.CreateNoWindow = true;

            Console.WriteLine($"PresentMon arguments: {_presentMonProcess.StartInfo.Arguments}");

            try
            {
                _presentMonProcess.Start();
                Console.WriteLine($"PresentMon started (PID: {_presentMonProcess.Id})");
                
                // Start reading output
                _ = Task.Run(async () =>
                {
                    while (!_presentMonProcess.HasExited)
                    {
                        var line = await _presentMonProcess.StandardOutput.ReadLineAsync();
                        if (!string.IsNullOrEmpty(line))
                        {
                            Console.WriteLine($"PresentMon Output: {line}");
                        }
                    }
                });

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
            if (_presentMonProcess != null && !_presentMonProcess.HasExited)
            {
                _presentMonProcess.Kill();
                _presentMonProcess.Dispose();
                _presentMonProcess = null;
            }
        }

        public MonitoringState GetMonitoringState()
        {
            return new MonitoringState
            {
                ProcessId = 0,
                ProcessName = "",
                WindowTitle = "Nothing to capture",
                IsMonitoring = _presentMonProcess != null && !_presentMonProcess.HasExited
            };
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                StopMonitoringAsync().Wait(1000);
                _disposed = true;
            }
        }
    }
}
