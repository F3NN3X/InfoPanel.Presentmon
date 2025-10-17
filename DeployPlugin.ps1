# Deploy InfoPanel PresentMon Plugin
# Automatically copies built plugin files to InfoPanel plugin directory

param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

# Source and destination paths
$ProjectRoot = "E:\GitHub\MyRepos\InfoPanel.Presentmon\InfoPanel.Presentmon"
$PluginDestination = "C:\ProgramData\InfoPanel\plugins\InfoPanel.Presentmon"

# Determine source path based on configuration
if ($Configuration -eq "Debug") {
    $SourcePath = "$ProjectRoot\bin\Debug\*"
} elseif ($Configuration -eq "Release") {
    $SourcePath = "$ProjectRoot\bin\Release\net8.0-windows\InfoPanel.Presentmon-v2.0.0\InfoPanel.Presentmon\*"
} else {
    Write-Error "Invalid configuration. Use 'Debug' or 'Release'"
    exit 1
}

Write-Host "=" * 60
Write-Host "InfoPanel PresentMon Plugin Deployment" -ForegroundColor Cyan
Write-Host "=" * 60
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host "Source:        $SourcePath" -ForegroundColor Yellow
Write-Host "Destination:   $PluginDestination" -ForegroundColor Yellow
Write-Host ""

# Check if source exists
if (-not (Test-Path $SourcePath.Replace("*", ""))) {
    Write-Error "Source path does not exist: $($SourcePath.Replace('*', ''))"
    Write-Host "Make sure you have built the project first:" -ForegroundColor Red
    Write-Host "  dotnet build InfoPanel.Presentmon\InfoPanel.Presentmon.csproj --configuration $Configuration" -ForegroundColor White
    exit 1
}

# Create destination directory if it doesn't exist
if (-not (Test-Path $PluginDestination)) {
    Write-Host "Creating plugin directory..." -ForegroundColor Green
    New-Item -ItemType Directory -Path $PluginDestination -Force | Out-Null
}

try {
    # Copy files
    Write-Host "Deploying plugin files..." -ForegroundColor Green
    Copy-Item -Path $SourcePath -Destination $PluginDestination -Recurse -Force
    
    # Verify deployment
    $deployedFiles = Get-ChildItem $PluginDestination -File
    Write-Host ""
    Write-Host "✅ Deployment successful!" -ForegroundColor Green
    Write-Host "Deployed files:" -ForegroundColor White
    
    foreach ($file in $deployedFiles) {
        $size = [math]::Round($file.Length / 1KB, 1)
        Write-Host "  - $($file.Name) ($size KB)" -ForegroundColor Gray
    }
    
    Write-Host ""
    Write-Host "Plugin deployed to: $PluginDestination" -ForegroundColor Cyan
    Write-Host "You can now test the plugin in InfoPanel." -ForegroundColor Green
    
} catch {
    Write-Error "Deployment failed: $($_.Exception.Message)"
    exit 1
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "1. Launch InfoPanel" -ForegroundColor White
Write-Host "2. Go to Plugins page" -ForegroundColor White
Write-Host "3. Enable 'InfoPanel.Presentmon' plugin" -ForegroundColor White
Write-Host "4. Check logs for any issues" -ForegroundColor White