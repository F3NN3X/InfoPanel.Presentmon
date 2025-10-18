using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PresentMon.BridgeContracts;

namespace PresentMon.BridgeService;

public sealed class PresentMonSessionManager
{
    private const string PipeName = "PresentMonBridgePipe";
    private readonly ILogger<PresentMonSessionManager> _logger;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly List<float> _frameTimes = new();
    private const int MaxFrameSamples = 1000;
    private const int DiagnosticLogLimit = 10;

    private Process? _presentMonProcess;
    private CancellationTokenSource? _presentMonCts;
    private Dictionary<string, int>? _columnIndexMap;
    private int _maxColumnIndex;
    private int _diagnosticLinesLogged;
    private TextWriter? _currentWriter;
    private readonly SemaphoreSlim _writerLock = new(1, 1);

    private float _lastGpuLatency;
    private float _lastGpuTime;
    private float _lastGpuBusy;
    private float _lastGpuWait;
    private float _lastDisplayLatency;
    private float _lastCpuBusy;
    private float _lastCpuWait;
    private float _lastGpuUtilization;
    private bool _useConsoleBuild;
    private string? _presentMonExecutablePath;

    private readonly string[] _frameTimeColumnCandidates =
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

    private readonly string[] _droppedColumnCandidates =
    {
        "Dropped",
        "WasDropped",
        "DroppedByDisplay"
    };

    public PresentMonSessionManager(ILogger<PresentMonSessionManager> logger)
    {
        _logger = logger;
    }

    public async Task AcceptAndProcessClientAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Waiting for PresentMon bridge client on pipe '{PipeName}'.", PipeName);

        await using var pipe = new NamedPipeServerStream(
            PipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Message,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Client connected to PresentMon bridge.");

    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
    using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };

        _currentWriter = writer;
        await SendMessageAsync(BridgeMessage.Ack("connected"), cancellationToken).ConfigureAwait(false);

        try
        {
            while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    break;
                }

                BridgeMessage? message = null;
                try
                {
                    message = JsonSerializer.Deserialize<BridgeMessage>(line, SerializerOptions);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize client message: {Payload}", line);
                    continue;
                }

                if (message == null)
                {
                    continue;
                }

                await HandleClientMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await StopMonitoringAsync().ConfigureAwait(false);
            _currentWriter = null;
            _logger.LogInformation("Client disconnected from PresentMon bridge.");
        }
    }

    private async Task HandleClientMessageAsync(BridgeMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case BridgeMessageType.StartMonitoringRequest:
                if (message.Start is null)
                {
                    await SendMessageAsync(BridgeMessage.CreateError("Missing start payload.", message.RequestId), cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (await StartMonitoringAsync(message.Start.ProcessId, message.Start.ProcessName, cancellationToken).ConfigureAwait(false))
                {
                    await SendMessageAsync(BridgeMessage.Ack(message.RequestId), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendMessageAsync(BridgeMessage.CreateError("Failed to start PresentMon session.", message.RequestId), cancellationToken).ConfigureAwait(false);
                }
                break;

            case BridgeMessageType.StopMonitoringRequest:
                await StopMonitoringAsync().ConfigureAwait(false);
                await SendMessageAsync(BridgeMessage.Ack(message.RequestId), cancellationToken).ConfigureAwait(false);
                break;

            case BridgeMessageType.Heartbeat:
                await SendMessageAsync(BridgeMessage.Ack(message.RequestId), cancellationToken).ConfigureAwait(false);
                break;

            default:
                _logger.LogWarning("Unsupported message type received: {Type}", message.Type);
                break;
        }
    }

    private async Task<bool> StartMonitoringAsync(uint processId, string processName, CancellationToken cancellationToken)
    {
        await StopMonitoringAsync().ConfigureAwait(false);

        var exePath = ResolvePresentMonExecutable(out var useConsoleBuild);
        if (string.IsNullOrEmpty(exePath))
        {
            _logger.LogError("PresentMon executable not found.\nExpected in PresentMonDataProvider folder next to service.");
            return false;
        }

        var providerDirectory = Path.GetDirectoryName(exePath)!;
        _logger.LogInformation("Using PresentMon executable '{ExePath}'.", exePath);

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

        _presentMonProcess = Process.Start(startInfo);
        if (_presentMonProcess == null)
        {
            _logger.LogError("Failed to launch PresentMon process.");
            return false;
        }

        _useConsoleBuild = useConsoleBuild;
        _presentMonExecutablePath = exePath;
        _presentMonProcess.EnableRaisingEvents = true;
        _presentMonProcess.Exited += async (_, _) =>
        {
            var exitCode = _presentMonProcess?.ExitCode;
            _logger.LogInformation("PresentMon process exited with code {ExitCode}.", exitCode);

            if (exitCode == 6)
            {
                await SendMessageAsync(BridgeMessage.CreateError("Access denied launching PresentMon."), CancellationToken.None).ConfigureAwait(false);
            }
        };

        _presentMonCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _frameTimes.Clear();
        ResetParsingState();

        _ = Task.Run(() => ProcessStdOutAsync(_presentMonProcess, _presentMonCts.Token), CancellationToken.None);
        _ = Task.Run(() => ProcessStdErrAsync(_presentMonProcess, _presentMonCts.Token), CancellationToken.None);

        return true;
    }

    private async Task StopMonitoringAsync()
    {
        if (_presentMonCts != null)
        {
            _presentMonCts.Cancel();
            _presentMonCts.Dispose();
            _presentMonCts = null;
        }

        if (_presentMonProcess != null)
        {
            try
            {
                if (!_presentMonProcess.HasExited)
                {
                    _presentMonProcess.Kill(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while killing PresentMon process.");
            }
            finally
            {
                _presentMonProcess.Dispose();
                _presentMonProcess = null;
            }
        }

    _frameTimes.Clear();
    ResetParsingState();

        if (!string.IsNullOrEmpty(_presentMonExecutablePath))
        {
            var providerDirectory = Path.GetDirectoryName(_presentMonExecutablePath);
            if (!string.IsNullOrEmpty(providerDirectory))
            {
                await TerminateExistingSessionAsync(_presentMonExecutablePath, providerDirectory, _useConsoleBuild).ConfigureAwait(false);
            }
        }

        _useConsoleBuild = false;
        _presentMonExecutablePath = null;
    }

    private async Task ProcessStdOutAsync(Process process, CancellationToken cancellationToken)
    {
        if (process.StandardOutput == null)
        {
            return;
        }

        try
        {
            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                ProcessOutputLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed while reading PresentMon stdout.");
        }
    }

    private async Task ProcessStdErrAsync(Process process, CancellationToken cancellationToken)
    {
        if (process.StandardError == null)
        {
            return;
        }

        try
        {
            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogWarning("PresentMon stderr: {Line}", line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed while reading PresentMon stderr.");
        }
    }

    private void ProcessOutputLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var columns = SplitCsvLine(line);
        if (columns.Count == 0)
        {
            return;
        }

        if (_columnIndexMap == null || string.Equals(columns[0], "Application", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseHeader(columns))
            {
                _logger.LogInformation("Detected PresentMon columns: {Columns}", string.Join(", ", _columnIndexMap!.Keys));
            }
            return;
        }

        if (_columnIndexMap == null || columns.Count <= _maxColumnIndex)
        {
            if (_diagnosticLinesLogged < DiagnosticLogLimit)
            {
                _logger.LogWarning("Skipping PresentMon data row due to insufficient columns: {Line}", line);
                _diagnosticLinesLogged++;
            }
            return;
        }

        if (ShouldSkipFrame(columns))
        {
            return;
        }

        if (!TryGetFrameTime(columns, out float frameTimeMs))
        {
            if (_diagnosticLinesLogged < DiagnosticLogLimit)
            {
                _logger.LogWarning("PresentMon frame time missing in row: {Line}", line);
                _diagnosticLinesLogged++;
            }
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
        else if (_diagnosticLinesLogged < DiagnosticLogLimit)
        {
            _logger.LogWarning("Frame time outside expected range: {Value} ms", frameTimeMs);
            _diagnosticLinesLogged++;
        }
    }

    private bool ShouldSkipFrame(IReadOnlyList<string> columns)
    {
        foreach (var column in _droppedColumnCandidates)
        {
            if (TryGetInt(columns, column, out int dropped) && dropped != 0)
            {
                return true;
            }
        }
        return false;
    }

    private bool TryGetFrameTime(IReadOnlyList<string> columns, out float frameTimeMs)
    {
        foreach (var column in _frameTimeColumnCandidates)
        {
            if (TryGetFloat(columns, column, out frameTimeMs))
            {
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
        if (string.IsNullOrEmpty(raw) || raw.Equals("NA", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out value))
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
        if (string.IsNullOrEmpty(raw) || raw.Equals("NA", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        return false;
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
                var utilization = (_lastGpuBusy / frameTimeMs) * 100f;
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

        var avgFrameTime = _frameTimes.Sum() / _frameTimes.Count;
        var fps = avgFrameTime > 0 ? 1000f / avgFrameTime : 0f;

        var sorted = _frameTimes.OrderByDescending(x => x).ToList();
        var onePercentIndex = Math.Max(0, (int)Math.Ceiling(_frameTimes.Count * 0.01f) - 1);
        var zeroPointOnePercentIndex = Math.Max(0, (int)Math.Ceiling(_frameTimes.Count * 0.001f) - 1);

        var onePercentLowFrame = sorted.ElementAtOrDefault(onePercentIndex);
        var zeroPointOnePercentLowFrame = sorted.ElementAtOrDefault(zeroPointOnePercentIndex);

        var onePercentLowFps = onePercentLowFrame > 0 ? 1000f / onePercentLowFrame : 0f;
        var zeroPointOnePercentLowFps = zeroPointOnePercentLowFrame > 0 ? 1000f / zeroPointOnePercentLowFrame : 0f;

        var payload = new BridgeMetricsPayload
        {
            AverageFrameTimeMs = avgFrameTime,
            FramesPerSecond = fps,
            OnePercentLowFps = onePercentLowFps,
            ZeroPointOnePercentLowFps = zeroPointOnePercentLowFps,
            GpuLatencyMs = _lastGpuLatency,
            GpuTimeMs = _lastGpuTime,
            GpuBusyMs = _lastGpuBusy,
            GpuWaitMs = _lastGpuWait,
            DisplayLatencyMs = _lastDisplayLatency,
            CpuBusyMs = _lastCpuBusy,
            CpuWaitMs = _lastCpuWait,
            GpuUtilizationPercent = _lastGpuUtilization
        };

        _ = SendMessageAsync(new BridgeMessage
        {
            Type = BridgeMessageType.Metrics,
            Metrics = payload
        }, CancellationToken.None);
    }

    private void ResetParsingState()
    {
        _columnIndexMap = null;
        _maxColumnIndex = 0;
        _diagnosticLinesLogged = 0;
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
        for (var i = 0; i < columns.Count; i++)
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

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
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

        for (var i = 0; i < result.Count; i++)
        {
            result[i] = result[i].Trim();
        }

        return result;
    }

    private async Task SendMessageAsync(BridgeMessage message, CancellationToken cancellationToken)
    {
        var writer = _currentWriter;
        if (writer == null)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(message, SerializerOptions);
        var lockAcquired = false;
        try
        {
            await _writerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            lockAcquired = true;

            await writer.WriteLineAsync(payload).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Pipe write failed.");
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "Pipe write invalid operation.");
        }
        finally
        {
            if (lockAcquired)
            {
                _writerLock.Release();
            }
        }
    }

    private static string BuildLaunchArguments(uint processId, string processName, bool useConsoleBuild)
    {
        if (useConsoleBuild)
        {
            var targetName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName
                : processName + ".exe";

            var args = new[]
            {
                $"--process_name \"{targetName}\"",
                "--output_stdout",
                "--stop_existing_session",
                "--terminate_on_proc_exit",
                "--no_console_stats",
                "--set_circular_buffer_size 8192",
                "--qpc_time",
                "--session_name PresentMonBridge"
            };

            return string.Join(' ', args);
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

    private static string? ResolvePresentMonExecutable(out bool useConsoleBuild)
    {
        useConsoleBuild = false;

        var baseDir = Path.GetFullPath(AppContext.BaseDirectory);
        if (string.IsNullOrEmpty(baseDir))
        {
            return null;
        }

        string? providerDir = Path.Combine(baseDir, "PresentMonDataProvider");
        if (!Directory.Exists(providerDir))
        {
            var parent = Path.GetDirectoryName(baseDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
            {
                var alternate = Path.Combine(parent, "PresentMonDataProvider");
                if (Directory.Exists(alternate))
                {
                    providerDir = alternate;
                }
            }
        }

        if (!Directory.Exists(providerDir))
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     "PresentMon-2.3.1-x64.exe",
                     "PresentMon-2.3.1-x64-DLSS4.exe",
                     "PresentMon-1.10.0-x64.exe",
                     "PresentMonDataProvider.exe"
                 })
        {
            var path = Path.Combine(providerDir, candidate);
            if (File.Exists(path))
            {
                useConsoleBuild = !string.Equals(Path.GetFileName(path), "PresentMonDataProvider.exe", StringComparison.OrdinalIgnoreCase);
                return path;
            }
        }

        return null;
    }

    private async Task TerminateExistingSessionAsync(string exePath, string workingDirectory, bool useConsoleBuild)
    {
        try
        {
            _logger.LogInformation("Terminating existing PresentMon session (consoleBuild={UseConsoleBuild}).", useConsoleBuild);
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
                _logger.LogInformation("terminate_existing_session exit code {ExitCode}.", process.ExitCode);
            }
            else
            {
                _logger.LogWarning("Failed to launch terminate_existing_session helper.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate existing PresentMon session.");
        }
    }

    private static string BuildTerminateArguments(bool useConsoleBuild) =>
        useConsoleBuild
            ? "--terminate_existing_session --session_name PresentMonBridge"
            : "-terminate_existing_session";
}
