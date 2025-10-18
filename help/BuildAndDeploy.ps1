# Build and Deploy InfoPanel PresentMon Plugin
# Builds the project and automatically deploys to InfoPanel

param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

Write-Host "=" * 60
Write-Host "InfoPanel PresentMon Plugin - Build & Deploy" -ForegroundColor Cyan
Write-Host "=" * 60
Write-Host ""

# Step 1: Build the project
Write-Host "Step 1: Building project ($Configuration)..." -ForegroundColor Yellow
try {
    dotnet build InfoPanel.Presentmon\InfoPanel.Presentmon.csproj --configuration $Configuration
    
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed with exit code $LASTEXITCODE"
    }
    
    Write-Host "✅ Build successful!" -ForegroundColor Green
} catch {
    Write-Error "Build failed: $($_.Exception.Message)"
    exit 1
}

Write-Host ""

# Step 2: Deploy the plugin
Write-Host "Step 2: Deploying plugin..." -ForegroundColor Yellow
try {
    & ".\DeployPlugin.ps1" -Configuration $Configuration
} catch {
    Write-Error "Deployment failed: $($_.Exception.Message)"
    exit 1
}

Write-Host ""
Write-Host "🚀 Build and deployment complete!" -ForegroundColor Green
Write-Host "The plugin is ready for testing in InfoPanel." -ForegroundColor Cyan