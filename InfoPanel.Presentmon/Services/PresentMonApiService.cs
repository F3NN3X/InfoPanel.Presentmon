using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using InfoPanel.Presentmon.Interop;
using InfoPanel.Presentmon.Models;

namespace InfoPanel.Presentmon.Services
{
    public class PresentMonApiService : IDisposable
    {
        public event EventHandler<FrameData>? MetricsUpdated;

        private PresentMonApi.PM_SESSION_HANDLE _session;
        private PresentMonApi.PM_FRAME_QUERY_HANDLE _frameQuery;
        private uint _targetProcessId = 0;
        private uint _blobSize = 0;
        private CancellationTokenSource? _pollingCts;
        private Task? _pollingTask;
        private readonly List<float> _frameTimes = new();
        private const int MaxFrameSamples = 1000;

        public async Task<bool> OpenSessionAsync(int pid)
        {
            try
            {
                uint result;
                
                // Check if session is already open
                if (_session.Value != IntPtr.Zero)
                {
                    // If already tracking this PID, return success
                    if (_targetProcessId == (uint)pid)
                    {
                        Console.WriteLine($"PresentMonAPI: Already tracking PID {pid}, session active");
                        return true;
                    }
                    
                    // Different PID - need to stop old tracking and start new
                    Console.WriteLine($"PresentMonAPI: Switching from PID {_targetProcessId} to {pid}");
                    await StopMonitoringAsync();
                }

                Console.WriteLine($"PresentMonAPI: Opening session for PID {pid}...");
                result = PresentMonApi.pmOpenSession(out _session);
                if (result != PresentMonApi.PM_STATUS_SUCCESS)
                {
                    Console.WriteLine($"PresentMonAPI: Failed to open session, error code: {result}");
                    
                    if (result == 14) // PM_STATUS_SESSION_ALREADY_OPEN
                    {
                        Console.WriteLine("PresentMonAPI: Another PresentMon session is already open.");
                        Console.WriteLine("PresentMonAPI: Close any other PresentMon applications and try again.");
                    }
                    
                    return false;
                }
                Console.WriteLine($"PresentMonAPI: Session opened");

                result = PresentMonApi.pmSetEtwFlushPeriod(_session, 8);
                if (result != PresentMonApi.PM_STATUS_SUCCESS)
                {
                    Console.WriteLine($"PresentMonAPI: Warning - Failed to set ETW flush period: {result}");
                }

                _targetProcessId = (uint)pid;
                result = PresentMonApi.pmStartTrackingProcess(_session, _targetProcessId);
                if (result != PresentMonApi.PM_STATUS_SUCCESS)
                {
                    Console.WriteLine($"PresentMonAPI: Failed to start tracking process, error code: {result}");
                    PresentMonApi.pmCloseSession(_session);
                    _session.Value = IntPtr.Zero;
                    return false;
                }

                Console.WriteLine($"PresentMonAPI: Now tracking PID {pid}");
                var queryElements = new[]
                {
                    new PresentMonApi.PM_QUERY_ELEMENT { metric = PresentMonApi.PM_METRIC_CPU_FRAME_TIME, stat = PresentMonApi.PM_STAT_NONE, deviceId = 0, arrayIndex = 0, dataOffset = 0, dataSize = 8 },
                    new PresentMonApi.PM_QUERY_ELEMENT { metric = PresentMonApi.PM_METRIC_GPU_TIME, stat = PresentMonApi.PM_STAT_NONE, deviceId = 0, arrayIndex = 0, dataOffset = 8, dataSize = 8 },
                    new PresentMonApi.PM_QUERY_ELEMENT { metric = PresentMonApi.PM_METRIC_GPU_LATENCY, stat = PresentMonApi.PM_STAT_NONE, deviceId = 0, arrayIndex = 0, dataOffset = 16, dataSize = 8 },
                    new PresentMonApi.PM_QUERY_ELEMENT { metric = PresentMonApi.PM_METRIC_DISPLAY_LATENCY, stat = PresentMonApi.PM_STAT_NONE, deviceId = 0, arrayIndex = 0, dataOffset = 24, dataSize = 8 }
                };

                result = PresentMonApi.pmRegisterFrameQuery(_session, out _frameQuery, queryElements, (ulong)queryElements.Length, out _blobSize);
                if (result != PresentMonApi.PM_STATUS_SUCCESS)
                {
                    Console.WriteLine($"PresentMonAPI: Failed to register frame query, error code: {result}");
                    await StopMonitoringAsync();
                    return false;
                }

                Console.WriteLine($"PresentMonAPI: Frame query registered (blob size: {_blobSize} bytes)");
                _pollingCts = new CancellationTokenSource();
                _pollingTask = Task.Run(() => PollFramesAsync(_pollingCts.Token));
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"PresentMonAPI: Session setup failed: {ex.Message}");
                return false;
            }
        }

        private async Task PollFramesAsync(CancellationToken ct)
        {
            Console.WriteLine("PresentMonAPI: Starting frame polling loop...");
            const int maxFramesPerPoll = 100;
            int blobBufferSize = (int)_blobSize * maxFramesPerPoll;
            IntPtr pBlobs = Marshal.AllocHGlobal(blobBufferSize);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        uint numFramesToRead = maxFramesPerPoll;
                        uint result = PresentMonApi.pmConsumeFrames(_frameQuery, _targetProcessId, pBlobs, ref numFramesToRead);

                        if (result == PresentMonApi.PM_STATUS_SUCCESS && numFramesToRead > 0)
                        {
                            Console.WriteLine($"PresentMonAPI: Consumed {numFramesToRead} frames");
                            for (int i = 0; i < numFramesToRead; i++)
                            {
                                IntPtr framePtr = IntPtr.Add(pBlobs, (int)(_blobSize * i));
                                double[] frameData = new double[4];
                                Marshal.Copy(framePtr, frameData, 0, 4);

                                if (frameData[0] > 0)
                                {
                                    ProcessFrame((float)frameData[0], (float)frameData[1], (float)frameData[2], (float)frameData[3]);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"PresentMonAPI: Error consuming frames: {ex.Message}");
                    }

                    await Task.Delay(16, ct);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pBlobs);
            }
        }

        private void ProcessFrame(float cpuFrameTimeMs, float gpuTimeMs, float gpuLatencyMs, float displayLatencyMs)
        {
            lock (_frameTimes)
            {
                _frameTimes.Add(cpuFrameTimeMs);
                if (_frameTimes.Count > MaxFrameSamples) _frameTimes.RemoveAt(0);

                if (_frameTimes.Count % 30 == 0 && _frameTimes.Count > 0)
                {
                    float avgFrameTime = _frameTimes.Average();
                    float avgFps = avgFrameTime > 0 ? 1000f / avgFrameTime : 0;

                    var sortedFrameTimes = _frameTimes.OrderByDescending(x => x).ToList();
                    int onePercentIndex = Math.Max(0, (int)(_frameTimes.Count * 0.01));
                    int zeroPointOnePercentIndex = Math.Max(0, (int)(_frameTimes.Count * 0.001));
                    
                    float onePercentLowFps = sortedFrameTimes[onePercentIndex] > 0 ? 1000f / sortedFrameTimes[onePercentIndex] : 0;
                    float zeroPointOnePercentLowFps = sortedFrameTimes[zeroPointOnePercentIndex] > 0 ? 1000f / sortedFrameTimes[zeroPointOnePercentIndex] : 0;

                    var frameData = new FrameData
                    {
                        Fps = avgFps,
                        FrameTimeMs = avgFrameTime,
                        OnePercentLowFps = onePercentLowFps,
                        ZeroPointOnePercentLowFps = zeroPointOnePercentLowFps,
                        GpuLatencyMs = gpuLatencyMs,
                        GpuTimeMs = gpuTimeMs,
                        GpuBusyMs = gpuTimeMs,
                        GpuWaitMs = 0,
                        DisplayLatencyMs = displayLatencyMs,
                        CpuBusyMs = 0,
                        CpuWaitMs = 0,
                        GpuUtilizationPercent = 0
                    };

                    MetricsUpdated?.Invoke(this, frameData);
                }
            }
        }

        public async Task StopMonitoringAsync()
        {
            Console.WriteLine("PresentMonAPI: Stop requested");
            _pollingCts?.Cancel();
            if (_pollingTask != null) await _pollingTask;

            if (_frameQuery.Value != IntPtr.Zero)
            {
                PresentMonApi.pmFreeFrameQuery(_frameQuery);
                _frameQuery.Value = IntPtr.Zero;
            }

            if (_session.Value != IntPtr.Zero && _targetProcessId != 0)
            {
                PresentMonApi.pmStopTrackingProcess(_session, _targetProcessId);
                _targetProcessId = 0;
            }

            if (_session.Value != IntPtr.Zero)
            {
                PresentMonApi.pmCloseSession(_session);
                _session.Value = IntPtr.Zero;
            }

            lock (_frameTimes) { _frameTimes.Clear(); }
        }

        public void Dispose()
        {
            try
            {
                _pollingCts?.Cancel();
                _pollingTask?.Wait(5000);
                _pollingCts?.Dispose();

                if (_frameQuery.Value != IntPtr.Zero) PresentMonApi.pmFreeFrameQuery(_frameQuery);
                if (_session.Value != IntPtr.Zero)
                {
                    if (_targetProcessId != 0) PresentMonApi.pmStopTrackingProcess(_session, _targetProcessId);
                    PresentMonApi.pmCloseSession(_session);
                }
            }
            catch { }
        }
    }
}
