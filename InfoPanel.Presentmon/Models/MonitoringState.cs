namespace InfoPanel.Presentmon.Models
{
    /// <summary>
    /// Represents the current monitoring state and detected process information
    /// </summary>
    public class MonitoringState
    {
        public uint ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string WindowTitle { get; set; } = string.Empty;
        public bool IsMonitoring { get; set; }
        public bool IsFullscreen { get; set; }
        
        public MonitoringState()
        {
        }
        
        public MonitoringState(uint processId, string processName, string windowTitle)
        {
            ProcessId = processId;
            ProcessName = processName;
            WindowTitle = windowTitle;
            IsFullscreen = true; // Assumed if detected by fullscreen service
        }
    }
}