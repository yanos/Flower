using System;
using System.Runtime.InteropServices;

namespace Flower.Diagnostics
{
    // One reading of what this process is costing the machine it runs on.
    //
    // CpuPercent is a rate, so it only exists relative to a previous reading -
    // null on the first sample from a given ResourceMonitor, and thereafter the
    // share of one wall-clock second spent on CPU, summed across threads. It is
    // deliberately not divided by ProcessorCount: on a phone the question being
    // asked is "is something spinning", and a single runaway decode thread
    // reading 100% is a clearer answer than the same thread reading 16% of six
    // cores.
    public readonly record struct ResourceSample(
        long    ManagedHeapBytes,
        long    ProcessMemoryBytes,
        long    TotalDeviceMemoryBytes,
        double? CpuPercent,
        int     Gen0Collections,
        int     Gen1Collections,
        int     Gen2Collections,
        int     ThreadCount);

    // Samples process CPU and memory, on every platform Flower has a head for.
    //
    // Instantiable rather than static because the CPU figure is a delta and the
    // baseline has to belong to whoever is asking: the settings screen polling
    // every second and the playback snapshot logging every ten would otherwise
    // consume each other's baselines and both report nonsense. One monitor per
    // consumer, no shared state.
    //
    // Written because there was no way to answer "is the phone hot because
    // Flower is busy, or busy because it is hot" from a log - the client log
    // carried decode and render counters and nothing at all about the process.
    public sealed class ResourceMonitor
    {
        private TimeSpan _lastCpu;
        private long     _lastTimestamp;
        private bool     _hasBaseline;

        public ResourceSample Sample()
        {
            double? cpuPercent = null;
            var cpu = TotalCpuTime();
            var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();

            if (_hasBaseline)
            {
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(_lastTimestamp, timestamp);
                // A zero or negative window says two samples landed in the same
                // tick; a percentage over no time at all is a divide-by-zero
                // dressed as data, so it is simply not reported.
                if (elapsed > TimeSpan.Zero)
                    cpuPercent = (cpu - _lastCpu).TotalSeconds / elapsed.TotalSeconds * 100.0;
            }

            _lastCpu       = cpu;
            _lastTimestamp = timestamp;
            _hasBaseline   = true;

            return new ResourceSample(
                ManagedHeapBytes:       GC.GetTotalMemory(forceFullCollection: false),
                ProcessMemoryBytes:     ProcessMemoryBytes(),
                TotalDeviceMemoryBytes: GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                CpuPercent:             cpuPercent,
                Gen0Collections:        GC.CollectionCount(0),
                Gen1Collections:        GC.CollectionCount(1),
                Gen2Collections:        GC.CollectionCount(2),
                ThreadCount:            ThreadCount());
        }

        private static TimeSpan TotalCpuTime()
        {
            try
            {
                return Environment.CpuUsage.TotalTime;
            }
            catch (PlatformNotSupportedException)
            {
                return TimeSpan.Zero;
            }
        }

        // Resident size, i.e. physical memory actually held.
        //
        // Apple platforms get the mach call rather than Environment.WorkingSet,
        // and iOS is the reason: WorkingSet reaches resident size through
        // libproc on macOS and returns 0 wherever the runtime has no
        // implementation, and 0 is indistinguishable from a real answer for a
        // reader of the settings screen. task_info with MACH_TASK_BASIC_INFO is
        // the same number the kernel shows Instruments, works identically on
        // macOS and iOS, and its struct layout has been stable for a decade -
        // unlike TASK_VM_INFO's phys_footprint, which is the number jetsam
        // enforces but sits at an offset that has moved between releases.
        private static long ProcessMemoryBytes()
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst())
            {
                var resident = MachResidentSize();
                if (resident > 0)
                    return resident;
            }

            try
            {
                return Environment.WorkingSet;
            }
            catch (PlatformNotSupportedException)
            {
                return 0;
            }
        }

        private const int MachTaskBasicInfo = 20;

        // sizeof(mach_task_basic_info) / sizeof(natural_t): three 64-bit sizes,
        // two 64-bit time_values, then two 32-bit fields = 48 bytes = 12 words.
        private const int MachTaskBasicInfoCount = 12;

        [StructLayout(LayoutKind.Sequential)]
        private struct MachTaskBasicInfoData
        {
            public ulong VirtualSize;
            public ulong ResidentSize;
            public ulong ResidentSizeMax;
            public long  UserTime;
            public long  SystemTime;
            public int   Policy;
            public int   SuspendCount;
        }

        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "mach_task_self")]
        private static extern uint MachTaskSelf();

        [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "task_info")]
        private static extern int TaskInfo(uint task, int flavor, ref MachTaskBasicInfoData info, ref int count);

        private static long MachResidentSize()
        {
            try
            {
                var info  = default(MachTaskBasicInfoData);
                var count = MachTaskBasicInfoCount;
                // KERN_SUCCESS is 0. Anything else means the flavor or the
                // count disagreed with this kernel, and the caller falls back.
                return TaskInfo(MachTaskSelf(), MachTaskBasicInfo, ref info, ref count) == 0
                    ? (long)info.ResidentSize
                    : 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return 0;
            }
        }

        // The CLR's own thread count, which is what a managed leak shows up in.
        // Process.Threads would count native ones too but drags
        // System.Diagnostics.Process onto every head to do it.
        private static int ThreadCount() => System.Threading.ThreadPool.ThreadCount + 1;
    }
}
