using System.Runtime.InteropServices;

namespace InfoPanel.Presentmon.Models
{
    /// <summary>
    /// Structure for PresentMonDataProvider shared memory
    /// Based on PresentMonDataBuffer.cpp from PresentMonDataProvider source
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PMDP_SHARED_MEMORY
    {
        public uint dwSignature;           // 'PMDP' = 0x504D4450
        public uint dwVersion;             // 0x00020000
        public uint dwFrameArrEntrySize;   // sizeof(PMDP_FRAME_DATA)
        public uint dwFrameArrOffset;      // Offset to frame array
        public uint dwFrameArrSize;        // Size of frame array
        public uint dwFrameCount;          // Total frames captured
        public uint dwFramePos;            // Current position in circular buffer
        public uint dwStatus;              // Status code
        // Frame array follows after this header
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PMDP_FRAME_DATA
    {
        public ulong qwPresentTime;        // Present timestamp
        public float fFrameTime;           // Frame time in ms
        public float fCpuFrameTime;        // CPU frame time
        public float fGpuTime;             // GPU time
        public float fGpuLatency;          // GPU latency
        public float fDisplayLatency;      // Display latency
        public uint dwFrameFlags;          // Frame flags
        // Additional fields may exist
    }

    public static class PMDPConstants
    {
        public const uint PMDP_SIGNATURE = 0x504D4450; // 'PMDP'
        public const uint PMDP_VERSION = 0x00020000;
        public const uint PMDP_STATUS_OK = 0;
        public const uint PMDP_STATUS_INIT_FAILED = 1;
    }
}
