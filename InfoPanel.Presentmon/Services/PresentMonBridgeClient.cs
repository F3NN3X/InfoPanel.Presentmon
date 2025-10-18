using System;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PresentMon.BridgeContracts;
using InfoPanel.Presentmon.Models;

namespace InfoPanel.Presentmon.Services;

internal sealed class PresentMonBridgeClient : IDisposable
{
    private const string PipeName = "PresentMonBridgePipe";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingRequests = new();
    private readonly object _connectionLock = new();

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readerCts;
    private Task? _readerTask;
    private bool _disposed;

    public event EventHandler<FrameData>? MetricsReceived;
    public event EventHandler? Disconnected;

    public bool IsConnected => _pipe?.IsConnected == true;

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(PresentMonBridgeClient));
        }

        if (IsConnected)
        {
            return true;
        }

        lock (_connectionLock)
        {
            if (IsConnected)
            {
                return true;
            }

            _pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
        }

        try
        {
            await _pipe!.ConnectAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            _pipe.ReadMode = PipeTransmissionMode.Message;
            _reader = new StreamReader(_pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true)
            {
                AutoFlush = true
            };

            _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _readerTask = Task.Run(() => ReaderLoopAsync(_readerCts.Token), CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bridge: failed to connect to service. {ex.Message}");
            CleanupConnection(notify: false);
            return false;
        }
    }

    public async Task<bool> StartMonitoringAsync(uint processId, string processName, CancellationToken cancellationToken)
    {
        if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = tcs;

        var message = new BridgeMessage
        {
            Type = BridgeMessageType.StartMonitoringRequest,
            RequestId = requestId,
            Start = new StartMonitoringPayload
            {
                ProcessId = processId,
                ProcessName = processName
            }
        };

        if (!await SendMessageAsync(message, cancellationToken).ConfigureAwait(false))
        {
            _pendingRequests.TryRemove(requestId, out _);
            return false;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

        try
        {
            return await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Bridge: start request timed out.");
            _pendingRequests.TryRemove(requestId, out _);
            return false;
        }
    }

    public async Task StopMonitoringAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return;
        }

        var requestId = Guid.NewGuid().ToString("N");
        var message = new BridgeMessage
        {
            Type = BridgeMessageType.StopMonitoringRequest,
            RequestId = requestId,
            Stop = new StopMonitoringPayload()
        };

        await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public void Disconnect(bool notify = false) => CleanupConnection(notify);

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected)
        {
            return true;
        }

        return await ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReaderLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _reader != null)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                BridgeMessage? message = null;
                try
                {
                    message = JsonSerializer.Deserialize<BridgeMessage>(line, SerializerOptions);
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"Bridge: failed to parse message. {ex.Message}");
                    continue;
                }

                if (message == null)
                {
                    continue;
                }

                HandleIncomingMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bridge: reader loop failed. {ex.Message}");
        }
        finally
        {
            CleanupConnection();
        }
    }

    private void HandleIncomingMessage(BridgeMessage message)
    {
        switch (message.Type)
        {
            case BridgeMessageType.Ack:
                if (!string.IsNullOrEmpty(message.RequestId) &&
                    _pendingRequests.TryRemove(message.RequestId, out var ackTcs))
                {
                    ackTcs.TrySetResult(true);
                }
                break;

            case BridgeMessageType.Error:
                if (!string.IsNullOrEmpty(message.RequestId) &&
                    _pendingRequests.TryRemove(message.RequestId, out var errorTcs))
                {
                    errorTcs.TrySetResult(false);
                }

                if (!string.IsNullOrWhiteSpace(message.Error))
                {
                    Console.WriteLine($"Bridge: error from service - {message.Error}");
                }
                break;

            case BridgeMessageType.Metrics:
                if (message.Metrics != null)
                {
                    var frame = new FrameData
                    {
                        FrameTimeMs = message.Metrics.AverageFrameTimeMs,
                        Fps = message.Metrics.FramesPerSecond,
                        OnePercentLowFps = message.Metrics.OnePercentLowFps,
                        ZeroPointOnePercentLowFps = message.Metrics.ZeroPointOnePercentLowFps,
                        GpuLatencyMs = message.Metrics.GpuLatencyMs,
                        GpuTimeMs = message.Metrics.GpuTimeMs,
                        GpuBusyMs = message.Metrics.GpuBusyMs,
                        GpuWaitMs = message.Metrics.GpuWaitMs,
                        DisplayLatencyMs = message.Metrics.DisplayLatencyMs,
                        CpuBusyMs = message.Metrics.CpuBusyMs,
                        CpuWaitMs = message.Metrics.CpuWaitMs,
                        GpuUtilizationPercent = message.Metrics.GpuUtilizationPercent,
                        Timestamp = DateTime.Now
                    };

                    MetricsReceived?.Invoke(this, frame);
                }
                break;

            case BridgeMessageType.Heartbeat:
                // Ignore for now.
                break;
        }
    }

    private async Task<bool> SendMessageAsync(BridgeMessage message, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsConnected || _writer == null)
            {
                return false;
            }

            var payload = JsonSerializer.Serialize(message, SerializerOptions);
            await _writer.WriteLineAsync(payload).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Bridge: failed to send message. {ex.Message}");
            return false;
        }
    }

    private void CleanupConnection(bool notify = true)
    {
        var wasConnected = IsConnected;

        foreach (var kvp in _pendingRequests)
        {
            kvp.Value.TrySetResult(false);
        }

        _pendingRequests.Clear();

        _readerCts?.Cancel();
        _readerCts?.Dispose();
        _readerCts = null;

        try
        {
            _readerTask?.Wait(1000);
        }
        catch
        {
        }

        _readerTask = null;

        _reader?.Dispose();
        _reader = null;

        _writer?.Dispose();
        _writer = null;

        if (_pipe != null)
        {
            try
            {
                _pipe.Dispose();
            }
            catch
            {
            }

            _pipe = null;
        }

        if (notify && wasConnected)
        {
            Console.WriteLine("Bridge: disconnected from service.");
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CleanupConnection(notify: false);
        GC.SuppressFinalize(this);
    }
}
