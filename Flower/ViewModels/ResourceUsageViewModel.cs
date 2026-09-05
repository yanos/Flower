using System;

using Avalonia.Threading;

using Flower.Diagnostics;

namespace Flower.ViewModels
{
    // What Flower is costing the device, for the settings screen.
    //
    // A debugging readout rather than a feature: the listener this app is for
    // has no use for a resident-set size. It exists because the alternative
    // when a phone got hot and stuttered was guessing - the client log carried
    // decode and render counters and nothing whatsoever about the process, so
    // "is it memory" could not be answered from the evidence, only argued
    // about.
    //
    // Polls only while it is on screen. A one-second timer that ran for the
    // life of the app would be a diagnostics feature that costs the battery it
    // is meant to help investigate.
    public sealed class ResourceUsageViewModel : ViewModelBase, IDisposable
    {
        private readonly ResourceMonitor _monitor = new();
        private DispatcherTimer? _timer;
        private bool _disposed;

        // Refresh() is public and the timer merely calls it, so a test can step
        // this without a dispatcher or a second of real time.
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

        public string CpuText     { get; private set; } = "-";
        public string MemoryText  { get; private set; } = "-";
        public string HeapText    { get; private set; } = "-";
        public string GcText      { get; private set; } = "-";
        public string ThreadsText { get; private set; } = "-";

        public void Start()
        {
            if (_disposed || _timer != null)
                return;

            // One sample immediately, so the screen is never blank for a
            // second while the first interval elapses - and because CpuPercent
            // needs a baseline before it can report anything at all, this is
            // the sample that establishes it.
            Refresh();

            _timer = new DispatcherTimer { Interval = PollInterval };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
        }

        public void Stop()
        {
            if (_timer == null)
                return;

            _timer.Stop();
            _timer = null;
        }

        public void Refresh()
        {
            var sample = _monitor.Sample();

            // Null on the very first sample of a session: a rate needs two
            // readings, and showing 0% would be a claim rather than an absence.
            CpuText = sample.CpuPercent is { } cpu ? $"{cpu:F1} %" : "measuring...";

            MemoryText = sample.TotalDeviceMemoryBytes > 0
                ? $"{Megabytes(sample.ProcessMemoryBytes)} MB of {Megabytes(sample.TotalDeviceMemoryBytes)} MB"
                : $"{Megabytes(sample.ProcessMemoryBytes)} MB";

            HeapText = $"{Megabytes(sample.ManagedHeapBytes)} MB";

            // Collection counts rather than a rate: what matters when chasing
            // a stutter is whether gen2 is moving at all, and a count that
            // stays put says that more plainly than 0.0/s does.
            GcText = $"gen0 {sample.Gen0Collections} / gen1 {sample.Gen1Collections} / gen2 {sample.Gen2Collections}";

            ThreadsText = sample.ThreadCount.ToString();

            OnPropertyChanged(nameof(CpuText));
            OnPropertyChanged(nameof(MemoryText));
            OnPropertyChanged(nameof(HeapText));
            OnPropertyChanged(nameof(GcText));
            OnPropertyChanged(nameof(ThreadsText));
        }

        private static long Megabytes(long bytes) => bytes / 1024 / 1024;

        public void Dispose()
        {
            _disposed = true;
            Stop();
        }
    }
}
