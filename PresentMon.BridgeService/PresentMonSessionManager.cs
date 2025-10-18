using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
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
    private const int MetricLogLimit = 5;

    private Process? _presentMonProcess;
    private CancellationTokenSource? _presentMonCts;
    private StreamReader? _presentMonStdOutReader;
    private StreamReader? _presentMonStdErrReader;
    private Dictionary<string, int>? _columnIndexMap;
    private int _maxColumnIndex;
    private int _diagnosticLinesLogged;
    private int _metricLinesLogged;
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
    private static bool _privilegesEnabled;
    private static readonly object PrivilegeLock = new();

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

    private bool TryGetPrimaryToken(uint targetProcessId, out IntPtr primaryToken, out string? errorMessage)
    {
        primaryToken = IntPtr.Zero;
        errorMessage = null;

        if (!TryResolveSessionId(targetProcessId, out var sessionId, out errorMessage))
        {
            return false;
        }

        if (!WTSQueryUserToken(sessionId, out var impersonationToken))
        {
            errorMessage = $"WTSQueryUserToken failed with error {Marshal.GetLastWin32Error()}.";
            return false;
        }

        try
        {
            const uint desiredAccess = TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!DuplicateTokenEx(impersonationToken, desiredAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primaryToken))
            {
                errorMessage = $"DuplicateTokenEx failed with error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            uint sessionIdCopy = sessionId;
            if (!SetTokenInformation(primaryToken, TokenSessionId, ref sessionIdCopy, sizeof(uint)))
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogDebug("SetTokenInformation(TokenSessionId) failed with error {Error}.", error);
            }

            return true;
        }
        finally
        {
            CloseHandle(impersonationToken);
        }
    }

    private bool TryResolveSessionId(uint processId, out uint sessionId, out string? errorMessage)
    {
        sessionId = 0;
        errorMessage = null;

        if (ProcessIdToSessionId(processId, out sessionId))
        {
            return true;
        }

        var win32Error = Marshal.GetLastWin32Error();
        _logger.LogDebug("ProcessIdToSessionId failed for process {ProcessId} with error {Error}.", processId, win32Error);

        try
        {
            using var process = Process.GetProcessById((int)processId);
            sessionId = (uint)process.SessionId;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to active console session for process {ProcessId}.", processId);
        }

        sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
        {
            errorMessage = "Unable to resolve active user session.";
            return false;
        }

        return true;
    }

    private void EnsureProcessPrivileges()
    {
        if (_privilegesEnabled)
        {
            return;
        }

        lock (PrivilegeLock)
        {
            if (_privilegesEnabled)
            {
                return;
            }

            EnablePrivilege("SeIncreaseQuotaPrivilege");
            EnablePrivilege("SeAssignPrimaryTokenPrivilege");
            _privilegesEnabled = true;
        }
    }

    private void EnablePrivilege(string privilege)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var tokenHandle))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilege, out var luid))
            {
                return;
            }

            var tokenPrivileges = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privilege = new LUID_AND_ATTRIBUTES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                }
            };

            AdjustTokenPrivileges(tokenHandle, false, ref tokenPrivileges, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
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

                var startResult = await StartMonitoringAsync(message.Start.ProcessId, message.Start.ProcessName, cancellationToken).ConfigureAwait(false);
                if (startResult.Success)
                {
                    await SendMessageAsync(BridgeMessage.Ack(message.RequestId), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var error = string.IsNullOrWhiteSpace(startResult.ErrorMessage)
                        ? "Failed to start PresentMon session."
                        : startResult.ErrorMessage;
                    await SendMessageAsync(BridgeMessage.CreateError(error, message.RequestId), cancellationToken).ConfigureAwait(false);
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

    private async Task<(bool Success, string? ErrorMessage)> StartMonitoringAsync(uint processId, string processName, CancellationToken cancellationToken)
    {
        try
        {
            await StopMonitoringAsync().ConfigureAwait(false);

            var exePath = ResolvePresentMonExecutable(out var useConsoleBuild);
            if (string.IsNullOrEmpty(exePath))
            {
                _logger.LogError("PresentMon executable not found.\nExpected in PresentMonDataProvider folder next to service.");
                return (false, "PresentMon executable not found in PresentMonDataProvider folder.");
            }

            var providerDirectory = Path.GetDirectoryName(exePath)!;
            _logger.LogInformation("Using PresentMon executable '{ExePath}'.", exePath);

            await TerminateExistingSessionAsync(exePath, providerDirectory, useConsoleBuild).ConfigureAwait(false);

            var arguments = BuildLaunchArguments(processId, processName, useConsoleBuild);

            if (!TryLaunchPresentMonProcess(
                    processId,
                    exePath,
                    arguments,
                    providerDirectory,
                    useConsoleBuild,
                    cancellationToken,
                    out var launchedProcess,
                    out var stdoutReader,
                    out var stderrReader,
                    out var launchError))
            {
                if (!string.IsNullOrWhiteSpace(launchError))
                {
                    _logger.LogError("Failed to launch PresentMon process: {Error}", launchError);
                    return (false, launchError);
                }

                _logger.LogError("Failed to launch PresentMon process.");
                return (false, "Failed to launch PresentMon process.");
            }

            if (launchedProcess == null)
            {
                _logger.LogError("PresentMon launch returned null process instance.");
                return (false, "Failed to launch PresentMon process.");
            }

            _useConsoleBuild = useConsoleBuild;
            _presentMonExecutablePath = exePath;
            _presentMonProcess = launchedProcess;
            _presentMonStdOutReader = stdoutReader;
            _presentMonStdErrReader = stderrReader;

            _presentMonProcess.EnableRaisingEvents = true;
            var processReference = _presentMonProcess;
            _presentMonProcess.Exited += async (_, _) =>
            {
                try
                {
                    var exitCode = processReference?.ExitCode;
                    _logger.LogInformation("PresentMon process exited with code {ExitCode}.", exitCode);

                    if (exitCode == 6)
                    {
                        await SendMessageAsync(BridgeMessage.CreateError("Access denied launching PresentMon."), CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error while handling PresentMon exit event.");
                }
            };

            _presentMonCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _frameTimes.Clear();
            ResetParsingState();

            if (_presentMonStdOutReader != null)
            {
                _ = Task.Run(() => ProcessStdOutAsync(_presentMonStdOutReader!, _presentMonCts.Token), CancellationToken.None);
            }

            if (_presentMonStdErrReader != null)
            {
                _ = Task.Run(() => ProcessStdErrAsync(_presentMonStdErrReader!, _presentMonCts.Token), CancellationToken.None);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start PresentMon session.");
            return (false, $"Exception starting PresentMon: {ex.Message}");
        }
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

        _presentMonStdOutReader?.Dispose();
        _presentMonStdOutReader = null;
        _presentMonStdErrReader?.Dispose();
        _presentMonStdErrReader = null;

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

    private async Task ProcessStdOutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                ProcessOutputLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed while reading PresentMon stdout.");
        }
    }

    private async Task ProcessStdErrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            string? line;
            while (!cancellationToken.IsCancellationRequested && (line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
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
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed while reading PresentMon stderr.");
        }
    }

    private bool TryLaunchPresentMonProcess(
        uint targetProcessId,
        string executablePath,
        string arguments,
        string workingDirectory,
        bool useConsoleBuild,
        CancellationToken cancellationToken,
        out Process? process,
        out StreamReader? stdoutReader,
        out StreamReader? stderrReader,
        out string? errorMessage)
    {
        process = null;
        stdoutReader = null;
        stderrReader = null;
        errorMessage = null;

        if (cancellationToken.IsCancellationRequested)
        {
            errorMessage = "Launch cancelled.";
            return false;
        }

        if (!useConsoleBuild)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = workingDirectory
            };

            process = Process.Start(startInfo);
            if (process == null)
            {
                errorMessage = "Failed to launch PresentMon process.";
                return false;
            }

            stdoutReader = process.StandardOutput;
            stderrReader = process.StandardError;
            return true;
        }

        EnsureProcessPrivileges();

        SafeFileHandle? stdOutRead = null;
        SafeFileHandle? stdOutWrite = null;
        SafeFileHandle? stdErrRead = null;
        SafeFileHandle? stdErrWrite = null;
        IntPtr primaryToken = IntPtr.Zero;

        try
        {
            if (!CreatePipe(out stdOutRead!, out stdOutWrite!, IntPtr.Zero, 0))
            {
                errorMessage = $"CreatePipe(stdout) failed with error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            if (!SetHandleInformation(stdOutRead!, HANDLE_FLAG_INHERIT, 0))
            {
                errorMessage = $"SetHandleInformation(stdout read) failed with error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            if (!SetHandleInformation(stdOutWrite!, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
            {
                errorMessage = $"SetHandleInformation(stdout write) failed with error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            if (!CreatePipe(out stdErrRead!, out stdErrWrite!, IntPtr.Zero, 0))
            {
                errorMessage = $"CreatePipe(stderr) failed with error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            if (!SetHandleInformation(stdErrRead!, HANDLE_FLAG_INHERIT, 0))
            {
                errorMessage = $"SetHandleInformation(stderr read) failed with error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            if (!SetHandleInformation(stdErrWrite!, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT))
            {
                errorMessage = $"SetHandleInformation(stderr write) failed with error {Marshal.GetLastWin32Error()}.";
                return false;
            }

            if (!TryGetPrimaryToken(targetProcessId, out primaryToken, out errorMessage))
            {
                return false;
            }

            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = STARTF_USESTDHANDLES,
                lpDesktop = "winsta0\\default",
                hStdOutput = stdOutWrite!.DangerousGetHandle(),
                hStdError = stdErrWrite!.DangerousGetHandle(),
                hStdInput = IntPtr.Zero
            };

            var commandLine = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                commandLine.Append('"').Append(executablePath).Append('"').Append(' ').Append(arguments);
            }
            else
            {
                commandLine.Append('"').Append(executablePath).Append('"');
            }

            IntPtr environment = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                {
                    environment = IntPtr.Zero;
                }

                var creationFlags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
                var success = CreateProcessAsUser(
                    primaryToken,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    creationFlags,
                    environment,
                    workingDirectory,
                    ref startupInfo,
                    out var processInfo);

                if (!success)
                {
                    errorMessage = $"CreateProcessAsUser failed with error {Marshal.GetLastWin32Error()}";
                    return false;
                }

                CloseHandle(processInfo.hThread);

                try
                {
                    process = Process.GetProcessById((int)processInfo.dwProcessId);
                }
                finally
                {
                    CloseHandle(processInfo.hProcess);
                }

                var stdoutStream = new FileStream(stdOutRead!, FileAccess.Read, 4096, false);
                var stderrStream = new FileStream(stdErrRead!, FileAccess.Read, 4096, false);
                stdoutReader = new StreamReader(stdoutStream, Encoding.UTF8);
                stderrReader = new StreamReader(stderrStream, Encoding.UTF8);

                stdOutRead = null;
                stdErrRead = null;

                return true;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
        }
        finally
        {
            if (primaryToken != IntPtr.Zero)
            {
                CloseHandle(primaryToken);
            }

            stdOutWrite?.Dispose();
            stdErrWrite?.Dispose();
            stdOutRead?.Dispose();
            stdErrRead?.Dispose();
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

        if (_metricLinesLogged < MetricLogLimit)
        {
            _logger.LogInformation("Metrics sample -> FPS: {Fps:F1}, AvgFrameTime: {FrameTime:F3} ms, 1% Low: {OnePercentLow:F1}, 0.1% Low: {ZeroPointOneLow:F1}", fps, avgFrameTime, onePercentLowFps, zeroPointOnePercentLowFps);
            _metricLinesLogged++;
        }

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
    _metricLinesLogged = 0;
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
                $"--process_id {processId}",
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
                     "PresentMonDataProvider.exe",
                     "PresentMon-1.10.0-x64.exe"
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

    private const int HANDLE_FLAG_INHERIT = 0x00000001;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenSessionId = 12;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES
    {
        public LUID Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID_AND_ATTRIBUTES Privilege;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(SafeHandle hObject, int dwMask, int dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint dwProcessId, out uint pSessionId);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint SessionId, out IntPtr phToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DuplicateTokenEx(IntPtr hExistingToken, uint dwDesiredAccess, IntPtr lpTokenAttributes, int ImpersonationLevel, int TokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, ref uint TokenInformation, uint TokenInformationLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);
}
