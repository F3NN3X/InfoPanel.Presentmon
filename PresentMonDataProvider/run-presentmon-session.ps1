[CmdletBinding(DefaultParameterSetName = "ByName")]
param(
    [Parameter(ParameterSetName = "ByName", Mandatory = $true)]
    [string]$ProcessName,

    [Parameter(ParameterSetName = "ById", Mandatory = $true)]
    [int]$ProcessId,

    [Parameter(ParameterSetName = "ByName", Mandatory = $false)]
    [Parameter(ParameterSetName = "ById", Mandatory = $false)]
    [int]$DurationSeconds = 15,

    [Parameter(ParameterSetName = "ByName", Mandatory = $false)]
    [Parameter(ParameterSetName = "ById", Mandatory = $false)]
    [string]$PresentMonExe = "PresentMon-2.3.1-x64.exe",

    [Parameter(ParameterSetName = "ByName", Mandatory = $false)]
    [Parameter(ParameterSetName = "ById", Mandatory = $false)]
    [string]$SessionName = "PresentMonDataProvider",

    [Parameter(ParameterSetName = "ByName", Mandatory = $false)]
    [Parameter(ParameterSetName = "ById", Mandatory = $false)]
    [string]$OutputFile,

    [Parameter(ParameterSetName = "ByName", Mandatory = $false)]
    [Parameter(ParameterSetName = "ById", Mandatory = $false)]
    [switch]$AppendCsv,

    [Parameter(ParameterSetName = "ByName", Mandatory = $false)]
    [Parameter(ParameterSetName = "ById", Mandatory = $false)]
    [switch]$NoTrackGpu,

    [Parameter(ParameterSetName = "ByName", Mandatory = $false)]
    [Parameter(ParameterSetName = "ById", Mandatory = $false)]
    [switch]$NoTrackInput
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-PresentMonPath {
    param([string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return (Resolve-Path -LiteralPath $Path).ProviderPath
    }

    $candidate = Join-Path -Path $PSScriptRoot -ChildPath $Path
    if (-not (Test-Path -LiteralPath $candidate)) {
        throw "Unable to locate PresentMon executable at '$candidate'."
    }

    return (Resolve-Path -LiteralPath $candidate).ProviderPath
}

function Resolve-TargetProcessId {
    param(
        [string]$Name,
        [int]$Id
    )

    if ($PSCmdlet.ParameterSetName -eq "ById") {
        try {
            $process = Get-Process -Id $Id -ErrorAction Stop
            return $process.Id
        }
        catch {
            throw "Process with Id $Id not found or inaccessible."
        }
    }

    $matching = Get-Process -Name $Name -ErrorAction SilentlyContinue | Sort-Object StartTime -Descending
    if (-not $matching) {
        throw "No running process named '$Name' was found."
    }

    return $matching[0].Id
}

function Stop-ExistingSession {
    param(
        [string]$ExePath,
        [string]$Session
    )

    $args = @("--session_name", $Session, "--terminate_existing_session")
    try {
        & $ExePath @args | Out-Null
    }
    catch {
        Write-Verbose "Terminate existing session returned an error (likely because none existed): $_"
    }
}

$exePath = Resolve-PresentMonPath -Path $PresentMonExe
$targetPid = Resolve-TargetProcessId -Name $ProcessName -Id $ProcessId

Write-Verbose "Using PresentMon executable at '$exePath'."
Write-Verbose "Target PID: $targetPid"
Write-Verbose "Session name: $SessionName"

Stop-ExistingSession -ExePath $exePath -Session $SessionName

$argumentList = @(
    "--session_name", $SessionName,
    "--stop_existing_session",
    "--process_id", $targetPid,
    "--output_stdout",
    "--qpc_time",
    "--no_console_stats",
    "--timed", $DurationSeconds.ToString()
)

if ($NoTrackGpu.IsPresent) {
    $argumentList += "--no_track_gpu"
}

if ($NoTrackInput.IsPresent) {
    $argumentList += "--no_track_input"
}

if ($OutputFile) {
    $outputPath = if ([System.IO.Path]::IsPathRooted($OutputFile)) { $OutputFile } else { Join-Path -Path $PSScriptRoot -ChildPath $OutputFile }
    $argumentList += @("--output_file", $outputPath)
    if ($AppendCsv.IsPresent) {
        $argumentList += "--csv_append"
    }
    Write-Verbose "Capturing CSV to '$outputPath'"
}

Write-Host "Launching PresentMon with arguments:" -ForegroundColor Cyan
Write-Host ("  " + ($argumentList -join " "))

try {
    $output = & $exePath @argumentList
    if ($output) {
        Write-Host "--- PresentMon stdout ---" -ForegroundColor Green
        $output | ForEach-Object { Write-Host $_ }
    }
    else {
        Write-Host "PresentMon did not emit stdout (CSV-only mode?)."
    }
}
catch {
    throw "PresentMon failed: $_"
}
