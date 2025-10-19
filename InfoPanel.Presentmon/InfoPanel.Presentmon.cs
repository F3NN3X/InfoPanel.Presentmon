using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Plugins;
using InfoPanel.Presentmon.Models;
using InfoPanel.Presentmon.Services;

namespace InfoPanel.Presentmon
{
    public class IPFpsPlugin : BasePlugin, IDisposable
    {
        private readonly PluginSensor _fpsSensor = new("fps", "Frames Per Second", 0, "FPS");
        private readonly PluginSensor _frameTimeSensor = new("frame time", "Frame Time", 0, "ms");
        private readonly PluginSensor _onePercentLowSensor = new("1% low", "1% Low FPS", 0, "FPS");
        private readonly PluginText _windowTitle = new("windowtitle", "Currently Capturing", "Nothing to capture");

        private readonly PresentMonProviderService _presentMonProviderService;
        private readonly FullscreenDetectionService _fullscreenDetectionService;

        private CancellationTokenSource? _monitoringCts;
        private Task? _monitoringTask;
        private MonitoringState? _currentState;
        private bool _disposed = false;

        public IPFpsPlugin()
            : base("presentmon", "PresentMon FPS", "Real-time FPS monitoring using PresentMonDataProvider - v2.0.0")
        {
            _presentMonProviderService = new PresentMonProviderService();
            _fullscreenDetectionService = new FullscreenDetectionService();
            _presentMonProviderService.MetricsUpdated += OnMetricsUpdated;
        }

        public override string? ConfigFilePath => null;
        public override TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

        public override void Initialize()
        {
            Console.WriteLine("Initializing PresentMon FPS Plugin v2.0.0...");
            Console.WriteLine("Starting monitoring task...");

            _monitoringCts = new CancellationTokenSource();
            _monitoringTask = Task.Run(() => MonitoringLoopAsync(_monitoringCts.Token));

            Console.WriteLine("Plugin initialized successfully");
        }

        private async Task MonitoringLoopAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("PresentMon Plugin: Monitoring loop started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var detectedState = await _fullscreenDetectionService.DetectFullscreenProcessAsync();

                    if (detectedState != null)
                    {
                        Console.WriteLine($"PresentMon Plugin: Detected fullscreen - {detectedState.ProcessName} (PID: {detectedState.ProcessId})");
                    }

                    if (detectedState != null && ShouldSwitchProcess(detectedState))
                    {
                        Console.WriteLine($"Switching to: {detectedState.ProcessName} (PID: {detectedState.ProcessId})");

                        // Don't stop the provider - just send it the new PID
                        bool success = await _presentMonProviderService.StartMonitoringAsync((int)detectedState.ProcessId);

                        if (success)
                        {
                            _currentState = detectedState;
                            _currentState.IsMonitoring = true;
                            _windowTitle.Value = detectedState.WindowTitle;
                        }
                    }
                    else if (_currentState?.IsMonitoring == true)
                    {
                        bool isValid = await _fullscreenDetectionService.IsProcessValidAsync(_currentState.ProcessId);
                        if (!isValid)
                        {
                            Console.WriteLine($"Process {_currentState.ProcessId} is no longer valid");
                            
                            // Send PID=0 to trigger RTSS mode, which resets buffer and waits for next game
                            // Provider will call StopConsoleStreaming() -> ResetFrameData() to clear dwFrameCount/dwFramePos
                            Console.WriteLine("PresentMon Plugin: Sending PID 0 to reset provider buffer (RTSS mode)");
                            await _presentMonProviderService.StartMonitoringAsync(0);
                            
                            // Give provider time to process the message and reset (provider sleeps 100ms between loops)
                            await Task.Delay(200, cancellationToken);
                            
                            _currentState.IsMonitoring = false;
                            _windowTitle.Value = "Nothing to capture";
                            ResetSensors();
                        }
                    }

                    await Task.Delay(1000, cancellationToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in monitoring loop: {ex.Message}");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private bool ShouldSwitchProcess(MonitoringState newState)
        {
            return _currentState == null || 
                   _currentState.ProcessId != newState.ProcessId || 
                   !_currentState.IsMonitoring;
        }

        private void OnMetricsUpdated(object? sender, FrameData frameData)
        {
            _fpsSensor.Value = frameData.Fps;
            _frameTimeSensor.Value = frameData.FrameTimeMs;
            _onePercentLowSensor.Value = frameData.OnePercentLowFps;
        }

        private void ResetSensors()
        {
            _fpsSensor.Value = 0;
            _frameTimeSensor.Value = 0;
            _onePercentLowSensor.Value = 0;
        }

        public override void Update()
        {
        }

        public override Task UpdateAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override void Load(List<IPluginContainer> containers)
        {
            var container = new PluginContainer("FPS Monitor");
            container.Entries.Add(_fpsSensor);
            container.Entries.Add(_frameTimeSensor);
            container.Entries.Add(_onePercentLowSensor);
            container.Entries.Add(_windowTitle);
            containers.Add(container);
            Console.WriteLine("FPS sensors loaded into UI");
        }

        public override void Close()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Console.WriteLine("Shutting down PresentMon plugin...");

                _monitoringCts?.Cancel();
                _monitoringTask?.Wait(5000);

                _presentMonProviderService?.StopMonitoringAsync().Wait(5000);
                _presentMonProviderService?.Dispose();
                _fullscreenDetectionService?.Dispose();

                _monitoringCts?.Dispose();

                Console.WriteLine("Plugin shutdown complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during disposal: {ex.Message}");
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}
