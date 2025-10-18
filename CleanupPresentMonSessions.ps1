# Clean up orphaned PresentMon ETW sessions
# Run this script if you get "Error code: 14 (SESSION_ALREADY_OPEN)"

Write-Host "Checking for orphaned PresentMon ETW sessions..." -ForegroundColor Cyan

# Get all active ETW sessions
$sessions = logman query -ets 2>&1 | Select-String "PresentMon"

if ($sessions) {
    Write-Host "Found PresentMon sessions:" -ForegroundColor Yellow
    $sessions | ForEach-Object { Write-Host "  $_" }
    
    # Stop each PresentMon session
    $sessions | ForEach-Object {
        $sessionName = $_.ToString().Split()[0].Trim()
        Write-Host "`nStopping session: $sessionName" -ForegroundColor Yellow
        
        $result = logman stop "$sessionName" -ets 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  ✓ Successfully stopped $sessionName" -ForegroundColor Green
        } else {
            Write-Host "  ✗ Failed to stop $sessionName" -ForegroundColor Red
            Write-Host "  Error: $result"
        }
    }
    
    Write-Host "`nCleanup complete! You can now start InfoPanel." -ForegroundColor Green
} else {
    Write-Host "No orphaned PresentMon sessions found." -ForegroundColor Green
    Write-Host "If you're still getting error 14, try rebooting Windows." -ForegroundColor Yellow
}

Write-Host "`nPress any key to exit..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
