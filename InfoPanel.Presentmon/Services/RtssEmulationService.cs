using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace InfoPanel.Presentmon.Services
{
    /// <summary>
    /// Creates a fake RTSS shared memory to trick PresentMonDataProvider.exe into running.
    /// PresentMonDataProvider checks for RTSSSharedMemoryV2 on startup - if not found, it exits immediately.
    /// </summary>
    public class RtssEmulationService : IDisposable
    {
        private MemoryMappedFile? _rtssSharedMemory;
        private bool _disposed = false;

        // RTSS Shared Memory Header (minimal structure to satisfy PresentMonDataProvider)
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RTSSSharedMemoryHeader
        {
            public uint Signature;              // 'RTSS' signature
            public uint Version;                // Structure version
            public uint HeaderSize;             // Size of this header
            public uint EntryCount;             // Number of entries
            public uint EntrySize;              // Size of each entry
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            public byte[] Reserved;             // Reserved space
        }

        public bool CreateFakeRtssMemory()
        {
            try
            {
                Console.WriteLine("RTSS Emulation: Creating fake RTSSSharedMemoryV2...");

                // Create the shared memory that PresentMonDataProvider looks for
                const int memorySize = 4096; // 4KB should be enough
                _rtssSharedMemory = MemoryMappedFile.CreateNew(
                    "RTSSSharedMemoryV2",
                    memorySize,
                    MemoryMappedFileAccess.ReadWrite
                );

                Console.WriteLine("RTSS Emulation: Created RTSSSharedMemoryV2 (4096 bytes)");

                // Write a fake header structure
                using (var accessor = _rtssSharedMemory.CreateViewAccessor())
                {
                    var header = new RTSSSharedMemoryHeader
                    {
                        Signature = 0x53535452, // 'RTSS' in little-endian
                        Version = 2,
                        HeaderSize = 256,
                        EntryCount = 0,
                        EntrySize = 0,
                        Reserved = new byte[256]
                    };

                    accessor.Write(0, ref header);
                    Console.WriteLine("RTSS Emulation: Wrote fake RTSS header");
                }

                Console.WriteLine("RTSS Emulation: Fake RTSS environment ready");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RTSS Emulation: Failed to create fake memory: {ex.Message}");
                
                // Check if it already exists (maybe RTSS is actually running)
                try
                {
                    _rtssSharedMemory = MemoryMappedFile.OpenExisting("RTSSSharedMemoryV2");
                    Console.WriteLine("RTSS Emulation: RTSSSharedMemoryV2 already exists (real RTSS is running?)");
                    return true;
                }
                catch
                {
                    Console.WriteLine("RTSS Emulation: Cannot create or open RTSSSharedMemoryV2");
                    return false;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                Console.WriteLine("RTSS Emulation: Disposing fake RTSS memory");
                _rtssSharedMemory?.Dispose();
                _rtssSharedMemory = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"RTSS Emulation: Error during dispose: {ex.Message}");
            }

            _disposed = true;
        }
    }
}
