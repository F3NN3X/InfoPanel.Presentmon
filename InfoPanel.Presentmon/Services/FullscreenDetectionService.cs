using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using InfoPanel.Presentmon.Models;
using Vanara.PInvoke;

namespace InfoPanel.Presentmon.Services
{
    public delegate bool EnumWindowsProc(HWND hWnd, IntPtr lParam);

    public class FullscreenDetectionService : IDisposable
    {
        private const int GWL_STYLE = -16;
        private const uint WS_CAPTION = 0x00C00000;
        private const uint WS_THICKFRAME = 0x00040000;

        private readonly string[] _systemProcessBlacklist = {
            "dwm", "winlogon", "csrss", "explorer", "taskmgr", "chrome", "firefox", 
            "msedge", "discord", "steam", "epicgameslauncher", "infopanel"
        };

        private readonly uint _selfPid;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowLong(HWND hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(HWND hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(HWND hWnd, out uint lpdwProcessId);

        public FullscreenDetectionService()
        {
            _selfPid = (uint)Process.GetCurrentProcess().Id;
        }

        public Task<MonitoringState?> DetectFullscreenProcessAsync()
        {
            return Task.Run(() =>
            {
                MonitoringState? detectedState = null;
                EnumWindows((hWnd, lParam) =>
                {
                    try
                    {
                        if (IsFullscreenWindow(hWnd))
                        {
                            GetWindowThreadProcessId(hWnd, out uint pid);
                            if (pid != _selfPid && !IsBlacklistedProcess(pid))
                            {
                                var processName = GetProcessName(pid);
                                var windowTitle = GetWindowTitle(hWnd);
                                if (!string.IsNullOrEmpty(processName))
                                {
                                    detectedState = new MonitoringState
                                    {
                                        ProcessId = pid,
                                        ProcessName = processName,
                                        WindowTitle = windowTitle ?? processName,
                                        IsMonitoring = false
                                    };
                                    return false;
                                }
                            }
                        }
                    }
                    catch { }
                    return true;
                }, IntPtr.Zero);
                return detectedState;
            });
        }

        public Task<bool> IsProcessValidAsync(uint pid)
        {
            return Task.Run(() =>
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    return !process.HasExited;
                }
                catch { return false; }
            });
        }

        private bool IsFullscreenWindow(HWND hWnd)
        {
            try
            {
                uint style = GetWindowLong(hWnd, GWL_STYLE);
                return (style & WS_CAPTION) == 0 || (style & WS_THICKFRAME) == 0;
            }
            catch { return false; }
        }

        private bool IsBlacklistedProcess(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                var processName = process.ProcessName.ToLowerInvariant();
                return Array.Exists(_systemProcessBlacklist, name =>
                    processName.Contains(name, StringComparison.OrdinalIgnoreCase));
            }
            catch { return true; }
        }

        private string? GetProcessName(uint pid)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName;
            }
            catch { return null; }
        }

        private string? GetWindowTitle(HWND hWnd)
        {
            try
            {
                var sb = new StringBuilder(256);
                int length = GetWindowText(hWnd, sb, sb.Capacity);
                return length > 0 ? sb.ToString() : null;
            }
            catch { return null; }
        }

        public void Dispose() { }
    }
}
