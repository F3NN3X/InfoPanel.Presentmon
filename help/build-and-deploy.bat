@echo off
echo InfoPanel PresentMon Plugin - Build and Deploy
echo ==============================================
echo.

echo Step 1: Building project (Debug)...
dotnet build InfoPanel.Presentmon\InfoPanel.Presentmon.csproj --configuration Debug

if %errorlevel% neq 0 (
    echo.
    echo ✗ Build failed!
    pause
    exit /b 1
)

echo.
echo ✓ Build successful!
echo.

echo Step 2: Deploying plugin...
call deploy-debug.bat

echo.
echo 🚀 Build and deployment complete!
echo The plugin is ready for testing in InfoPanel.