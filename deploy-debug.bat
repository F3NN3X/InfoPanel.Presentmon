@echo off
echo InfoPanel PresentMon Plugin - Quick Deploy
echo =========================================
echo.

set SOURCE=InfoPanel.Presentmon\bin\Debug\*
set DEST=C:\ProgramData\InfoPanel\plugins\InfoPanel.Presentmon

echo Deploying from: %SOURCE%
echo Deploying to:   %DEST%
echo.

if not exist "%DEST%" (
    echo Creating plugin directory...
    mkdir "%DEST%"
)

echo Copying files...
xcopy "%SOURCE%" "%DEST%" /E /Y /Q

if %errorlevel% == 0 (
    echo.
    echo ✓ Plugin deployed successfully!
    echo.
    echo Next: Launch InfoPanel and enable the plugin
) else (
    echo.
    echo ✗ Deployment failed!
    echo Make sure you built the project first:
    echo   dotnet build InfoPanel.Presentmon\InfoPanel.Presentmon.csproj --configuration Debug
)

echo.
pause