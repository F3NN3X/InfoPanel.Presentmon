using System;
using System.Runtime.InteropServices;

namespace InfoPanel.Presentmon.Interop
{
    /// <summary>
    /// P/Invoke declarations for PresentMonAPI2.dll - Intel's official PresentMon API
    /// Based on PresentMonAPI2.h header file (API version 3.2)
    /// </summary>
    public static class PresentMonApi
    {
        // Use PresentMonAPI2.dll from PresentMonDataProvider folder
        private const string DllName = "PresentMonDataProvider\\PresentMonAPI2.dll";
        private const CallingConvention CallConv = CallingConvention.Cdecl;

        // Error codes / PM_STATUS enum
        public const uint PM_STATUS_SUCCESS = 0;
        public const uint PM_STATUS_FAILURE = 1;
        public const uint PM_STATUS_BAD_ARGUMENT = 2;
        public const uint PM_STATUS_BAD_HANDLE = 3;
        public const uint PM_STATUS_SERVICE_ERROR = 4;

        // PM_METRIC enum values we care about
        public const uint PM_METRIC_CPU_FRAME_TIME = 8;
        public const uint PM_METRIC_DISPLAYED_FPS = 11;
        public const uint PM_METRIC_GPU_TIME = 13;
        public const uint PM_METRIC_GPU_LATENCY = 23;
        public const uint PM_METRIC_DISPLAY_LATENCY = 24;

        // PM_STAT enum
        public const uint PM_STAT_NONE = 0;
        public const uint PM_STAT_AVG = 1;

        // Handle types
        [StructLayout(LayoutKind.Sequential)]
        public struct PM_SESSION_HANDLE
        {
            public IntPtr Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PM_FRAME_QUERY_HANDLE
        {
            public IntPtr Value;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PM_QUERY_ELEMENT
        {
            public uint metric;           // PM_METRIC
            public uint stat;             // PM_STAT
            public uint deviceId;
            public uint arrayIndex;
            public ulong dataOffset;
            public ulong dataSize;
        }

        // Session management
        [DllImport(DllName, CallingConvention = CallConv)]
        public static extern uint pmOpenSession(out PM_SESSION_HANDLE pHandle);

        [DllImport(DllName, CallingConvention = CallConv)]
        public static extern uint pmCloseSession(PM_SESSION_HANDLE handle);

        [DllImport(DllName, CallingConvention = CallConv)]
        public static extern uint pmStartTrackingProcess(PM_SESSION_HANDLE handle, uint processId);

        [DllImport(DllName, CallingConvention = CallConv)]
        public static extern uint pmStopTrackingProcess(PM_SESSION_HANDLE handle, uint processId);

        // Frame query registration and consumption
        [DllImport(DllName, CallingConvention = CallConv)]
        public static extern uint pmRegisterFrameQuery(
            PM_SESSION_HANDLE sessionHandle,
            out PM_FRAME_QUERY_HANDLE pHandle,
            [In] PM_QUERY_ELEMENT[] pElements,
            ulong numElements,
            out uint pBlobSize);

        [DllImport(DllName, CallingConvention = CallConv)]
        public static extern uint pmConsumeFrames(
            PM_FRAME_QUERY_HANDLE handle,
            uint processId,
            IntPtr pBlobs,
            ref uint pNumFramesToRead);

        [DllImport(DllName, CallingConvention = CallConv)]
        public static extern uint pmFreeFrameQuery(PM_FRAME_QUERY_HANDLE handle);

        // ETW flush period
        [DllImport(DllName, CallingConvention = CallConv)]
        public static extern uint pmSetEtwFlushPeriod(PM_SESSION_HANDLE handle, uint periodMs);
    }
}
