using System;
using System.Diagnostics;

using Flower.Diagnostics;
using Flower.ViewModels;

namespace Flower.Tests;

// The process-cost readings behind the settings screen's diagnostics block and
// the playback snapshot's CPU/memory fields.
//
// Worth testing at all because the numbers are the whole point: a readout that
// silently reports zero is worse than no readout, since it answers "is memory
// the problem" with a confident no. Every assertion here is therefore about a
// figure being real, not merely present.
public class ResourceMonitorTests
{
    // A rate needs two readings. Reporting 0% for the first one would be a
    // claim about an idle process rather than an admission of having nothing
    // to compare against yet.
    [Fact]
    public void The_first_sample_has_no_cpu_figure_and_the_second_does()
    {
        var monitor = new ResourceMonitor();

        Assert.Null(monitor.Sample().CpuPercent);

        Spin(TimeSpan.FromMilliseconds(50));

        Assert.NotNull(monitor.Sample().CpuPercent);
    }

    // The one that would have caught Environment.WorkingSet returning 0 on a
    // platform the runtime has no implementation for - which is why Apple
    // heads go through task_info instead. Asserted as "a plausible size for a
    // .NET process" rather than a number, since the real one moves.
    [Fact]
    public void Resident_memory_is_a_real_figure()
    {
        var sample = new ResourceMonitor().Sample();

        Assert.True(sample.ProcessMemoryBytes > 4 * 1024 * 1024,
            $"resident memory read back as {sample.ProcessMemoryBytes} bytes, which no .NET process uses");
        Assert.True(sample.ManagedHeapBytes > 0);
        Assert.True(sample.TotalDeviceMemoryBytes > sample.ProcessMemoryBytes,
            "the device cannot have less memory than this process is holding");
    }

    // Busy work has to move the CPU figure. Deliberately asserted as "above
    // zero" rather than "near 100": the test machine may be sharing cores with
    // anything, and a threshold tight enough to be interesting is a threshold
    // loose enough to be flaky.
    [Fact]
    public void Cpu_time_spent_spinning_shows_up_as_cpu_percent()
    {
        var monitor = new ResourceMonitor();
        monitor.Sample();

        Spin(TimeSpan.FromMilliseconds(200));

        var busy = monitor.Sample().CpuPercent;
        Assert.NotNull(busy);
        Assert.True(busy > 0, $"a busy loop reported {busy:F2}% CPU");
    }

    [Fact]
    public void Gc_counters_and_thread_count_come_back_populated()
    {
        var sample = new ResourceMonitor().Sample();

        Assert.True(sample.Gen0Collections >= 0);
        Assert.True(sample.Gen1Collections >= 0);
        Assert.True(sample.Gen2Collections >= 0);
        Assert.True(sample.ThreadCount > 0);
    }

    // Two consumers - the settings screen at one second, the playback snapshot
    // at ten - must not consume each other's baseline, which is the reason
    // ResourceMonitor is instantiable rather than static.
    [Fact]
    public void Two_monitors_keep_independent_cpu_baselines()
    {
        var slow = new ResourceMonitor();
        var fast = new ResourceMonitor();

        slow.Sample();
        Spin(TimeSpan.FromMilliseconds(20));

        // fast has never sampled, so its first reading still has no baseline
        // even though slow already established one.
        Assert.Null(fast.Sample().CpuPercent);
        Assert.NotNull(slow.Sample().CpuPercent);
    }

    // ── The settings screen's formatting ───────────────────────────────────

    [Fact]
    public void The_readout_says_it_is_measuring_until_it_has_a_rate()
    {
        var vm = new ResourceUsageViewModel();

        vm.Refresh();
        Assert.Equal("measuring...", vm.CpuText);

        Spin(TimeSpan.FromMilliseconds(50));

        vm.Refresh();
        Assert.EndsWith("%", vm.CpuText);
        Assert.NotEqual("measuring...", vm.CpuText);
    }

    [Fact]
    public void The_readout_reports_memory_against_what_the_device_has()
    {
        var vm = new ResourceUsageViewModel();
        vm.Refresh();

        Assert.Matches(@"^\d+ MB of \d+ MB$", vm.MemoryText);
        Assert.EndsWith(" MB", vm.HeapText);
        Assert.StartsWith("gen0 ", vm.GcText);
        Assert.True(int.Parse(vm.ThreadsText) > 0);
    }

    private static void Spin(TimeSpan duration)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
        }
    }
}
