# Test PresentMon Plugin Setup
# Validates environment and configuration before testing

$ErrorActionPreference = "Stop"

Write-Host "=" * 70
Write-Host "InfoPanel PresentMon Plugin - Environment Validation" -ForegroundColor Cyan
Write-Host "=" * 70
Write-Host ""

# Test 1: Check for conflicting plugins
Write-Host "[Test 1] Checking for conflicting FPS plugins..." -ForegroundColor Yellow
$fpsPlugins = Get-ChildItem -Path "C:\ProgramData\InfoPanel" -Filter "*FPS*.dll" -Recurse -ErrorAction SilentlyContinue
if ($fpsPlugins.Count -eq 0) {
    Write-Host "✅ No conflicting FPS plugins found" -ForegroundColor Green
} elseif ($fpsPlugins.Count -eq 1 -and $fpsPlugins[0].DirectoryName -like "*InfoPanel.Presentmon*") {
    Write-Host "✅ Only new PresentMon plugin found" -ForegroundColor Green
} else {
    Write-Host "❌ MULTIPLE FPS plugins detected!" -ForegroundColor Red
    foreach ($plugin in $fpsPlugins) {
        Write-Host "   - $($plugin.FullName)" -ForegroundColor Red
    }
    Write-Host "   Action: Remove old plugins before testing" -ForegroundColor Yellow
    exit 1
}
Write-Host ""

# Test 2: Verify plugin deployment
Write-Host "[Test 2] Verifying plugin deployment..." -ForegroundColor Yellow
$pluginPath = "C:\ProgramData\InfoPanel\plugins\InfoPanel.Presentmon"
$requiredFiles = @(
    "InfoPanel.Presentmon.dll",
    "PresentMonDataProvider\PresentMon-2.3.1-x64-DLSS4.exe"
)

$allFilesPresent = $true
foreach ($file in $requiredFiles) {
    $fullPath = Join-Path $pluginPath $file
    if (Test-Path $fullPath) {
        Write-Host "   ✓ $file" -ForegroundColor Gray
    } else {
        Write-Host "   ✗ $file MISSING!" -ForegroundColor Red
        $allFilesPresent = $false
    }
}

if ($allFilesPresent) {
    Write-Host "✅ All required files present" -ForegroundColor Green
} else {
    Write-Host "❌ Missing required files!" -ForegroundColor Red
    Write-Host "   Action: Run DeployPlugin.ps1" -ForegroundColor Yellow
    exit 1
}
Write-Host ""

# Test 3: Check InfoPanel process
Write-Host "[Test 3] Checking InfoPanel process..." -ForegroundColor Yellow
$infoPanelProcesses = Get-Process -Name "InfoPanel" -ErrorAction SilentlyContinue
if ($infoPanelProcesses) {
    Write-Host "⚠️  InfoPanel is currently running" -ForegroundColor Yellow
    Write-Host "   For clean testing, close InfoPanel and restart it" -ForegroundColor Yellow
    Write-Host "   PIDs: $($infoPanelProcesses.Id -join ', ')" -ForegroundColor Gray
} else {
    Write-Host "✅ InfoPanel not running (ready for fresh start)" -ForegroundColor Green
}
Write-Host ""

# Test 4: Check for orphaned PresentMon processes
Write-Host "[Test 4] Checking for orphaned PresentMon processes..." -ForegroundColor Yellow
$presentMonProcesses = Get-Process | Where-Object { $_.Name -like "*PresentMon*" }
if ($presentMonProcesses) {
    Write-Host "⚠️  Found running PresentMon processes:" -ForegroundColor Yellow
    foreach ($proc in $presentMonProcesses) {
        Write-Host "   - $($proc.Name) (PID: $($proc.Id))" -ForegroundColor Gray
    }
    Write-Host "   These will be cleaned up automatically on plugin start" -ForegroundColor Gray
} else {
    Write-Host "✅ No orphaned PresentMon processes" -ForegroundColor Green
}
Write-Host ""

# Test 5: Check administrator privileges
Write-Host "[Test 5] Checking administrator privileges..." -ForegroundColor Yellow
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
    Write-Host "✅ Running with administrator privileges" -ForegroundColor Green
} else {
    Write-Host "⚠️  NOT running as administrator" -ForegroundColor Yellow
    Write-Host "   InfoPanel should be run as administrator for ETW access" -ForegroundColor Yellow
    Write-Host "   Right-click InfoPanel → Run as administrator" -ForegroundColor Gray
}
Write-Host ""

# Test 6: Test PresentMon executable directly
Write-Host "[Test 6] Testing PresentMon executable..." -ForegroundColor Yellow
$presentMonExe = Join-Path $pluginPath "PresentMonDataProvider\PresentMon-2.3.1-x64-DLSS4.exe"
if (Test-Path $presentMonExe) {
    Write-Host "   Executable found: $presentMonExe" -ForegroundColor Gray
    
    # Check file properties
    $fileInfo = Get-Item $presentMonExe
    $fileSizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
    Write-Host "   Size: $fileSizeMB MB" -ForegroundColor Gray
    Write-Host "   Last Modified: $($fileInfo.LastWriteTime)" -ForegroundColor Gray
    
    # Try to get version info
    try {
        $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($presentMonExe)
        if ($versionInfo.FileVersion) {
            Write-Host "   Version: $($versionInfo.FileVersion)" -ForegroundColor Gray
        }
    } catch {
        Write-Host "   (Version info not available)" -ForegroundColor DarkGray
    }
    
    Write-Host "✅ PresentMon executable validated" -ForegroundColor Green
} else {
    Write-Host "❌ PresentMon executable not found!" -ForegroundColor Red
    exit 1
}
Write-Host ""

# Summary
Write-Host "=" * 70
Write-Host "VALIDATION SUMMARY" -ForegroundColor Cyan
Write-Host "=" * 70

if (-not $allFilesPresent) {
    Write-Host "❌ FAILED: Missing required files" -ForegroundColor Red
    exit 1
}

if ($fpsPlugins.Count -gt 1) {
    Write-Host "❌ FAILED: Multiple FPS plugins detected" -ForegroundColor Red
    exit 1
}

Write-Host "✅ Environment is ready for testing!" -ForegroundColor Green
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "1. Close InfoPanel if running" -ForegroundColor White
Write-Host "2. Launch InfoPanel (as administrator)" -ForegroundColor White
Write-Host "3. Open a game in fullscreen" -ForegroundColor White
Write-Host "4. Check debug.log for:" -ForegroundColor White
Write-Host "   - 'PresentMon Plugin: Monitoring loop started'" -ForegroundColor Gray
Write-Host "   - 'PresentMon Plugin: Detected fullscreen - <GameName>'" -ForegroundColor Gray
Write-Host "   - 'PresentMon: CSV Header: Application,ProcessID,...'" -ForegroundColor Gray
Write-Host "   - 'Averaged: FrameTime=XX.XXms, FPS=XX.XX, ...'" -ForegroundColor Gray
Write-Host ""
Write-Host "Debug log location: E:\GitHub\PublicRepos\infopanel\InfoPanel\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\debug.log" -ForegroundColor DarkGray
