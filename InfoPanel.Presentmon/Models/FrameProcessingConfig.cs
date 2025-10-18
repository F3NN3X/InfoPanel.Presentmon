namespace InfoPanel.Presentmon.Models
{
    /// <summary>
    /// Configuration for frame processing and performance analysis
    /// </summary>
    public class FrameProcessingConfig
    {
        /// <summary>
        /// Maximum number of frame samples to keep for rolling window calculations
        /// </summary>
        public int MaxSamples { get; set; } = 1000;
        
        /// <summary>
        /// Update interval in milliseconds for polling operations
        /// </summary>
        public int UpdateIntervalMs { get; set; } = 16;
        
        /// <summary>
        /// Minimum frame time threshold in milliseconds to filter out invalid data
        /// </summary>
        public float MinFrameTimeMs { get; set; } = 0.1f;
        
        /// <summary>
        /// Maximum frame time threshold in milliseconds to filter out invalid data
        /// </summary>
        public float MaxFrameTimeMs { get; set; } = 1000f;
        
        /// <summary>
        /// Session name prefix for PresentMon ETW sessions
        /// </summary>
        public string SessionNamePrefix { get; set; } = "InfoPanel";
    }
}