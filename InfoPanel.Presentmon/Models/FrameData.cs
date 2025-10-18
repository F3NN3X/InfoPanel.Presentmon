using System;

namespace InfoPanel.Presentmon.Models
{
    /// <summary>
    /// Represents frame performance data from PresentMon
    /// </summary>
    public class FrameData
    {
        public float Fps { get; set; }
        public float FrameTimeMs { get; set; }
        public float OnePercentLowFps { get; set; }
        public float ZeroPointOnePercentLowFps { get; set; }
        public float GpuLatencyMs { get; set; }
        public float GpuTimeMs { get; set; }
        public float GpuBusyMs { get; set; }
        public float GpuWaitMs { get; set; }
        public float DisplayLatencyMs { get; set; }
        public float CpuBusyMs { get; set; }
        public float CpuWaitMs { get; set; }
        public float GpuUtilizationPercent { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        
        public FrameData()
        {
        }
        
        public FrameData(float frameTimeMs)
        {
            FrameTimeMs = frameTimeMs;
            Fps = frameTimeMs > 0 ? 1000f / frameTimeMs : 0;
            Timestamp = DateTime.Now;
        }
    }
}