using System;
using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Flower.Controls;

// A frame-rate readout for finding out why scrolling is not smooth, on the
// device rather than in a profiler. Avalonia has one of its own
// (RendererDiagnostics.DebugOverlays), but it draws at a fixed spot in the top
// corner - under a phone's status bar and Dynamic Island - and scales its graphs
// to a 60Hz frame, so a 120Hz screen's budget is half of what it shows as fine.
//
// Once a second it reports, for that second:
//   - frames: how many frames the UI thread was given, from Avalonia's own frame
//     callback. While something moves, this is the rate the screen is actually
//     being updated at; at rest Avalonia ticks on a 16ms timer, so read it during
//     a scroll.
//   - worst: the longest gap between two frames, and missed: how many gaps were
//     long enough to have skipped at least one refresh of a 120Hz display.
//   - UI: the longest the UI thread stayed busy after a frame began - layout,
//     input, and recording what to draw. Frames missed while this stays well
//     under 8ms were lost on the render thread or the GPU, not here.
//   - GC: collections in that second, by generation, and the time the runtime
//     says it spent paused for them where it reports one (Mono may not).
public sealed class FrameMeter : Border
{
    // One 120Hz refresh is 8.33ms; a gap past one and a half of them means a
    // refresh went by with nothing new to show.
    private const double MissedFrameMs = 1000.0 / 120 * 1.5;

    private readonly TextBlock _text = new()
    {
        FontSize = 12,
        FontFamily = new FontFamily("Menlo, Consolas, monospace"),
        Foreground = Brushes.White,
    };

    private bool _running;
    private int _generation;
    private long _last;
    private long _secondStarted;
    private int _frames;
    private double _worstGapMs;
    private int _missed;
    private double _worstBusyMs;
    private int _gen0, _gen1, _gen2;
    private TimeSpan _paused;

    public FrameMeter()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0));
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(8, 4);
        IsHitTestVisible = false;
        Child = _text;
        _text.Text = "measuring...";
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            UpdateRunning();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UpdateRunning();
    }

    private void UpdateRunning()
    {
        var run = IsVisible && TopLevel.GetTopLevel(this) != null;
        if (run == _running)
            return;
        _running = run;
        _generation++;
        if (!run)
            return;

        _last = 0;
        StartSecond(Stopwatch.GetTimestamp());
        RequestFrame();
    }

    private void RequestFrame()
    {
        var generation = _generation;
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(_ =>
        {
            if (generation == _generation)
                OnFrame();
        });
    }

    private void OnFrame()
    {
        var now = Stopwatch.GetTimestamp();
        if (_last != 0)
        {
            var gap = Stopwatch.GetElapsedTime(_last, now).TotalMilliseconds;
            _frames++;
            _worstGapMs = Math.Max(_worstGapMs, gap);
            if (gap > MissedFrameMs)
                _missed++;
        }
        _last = now;

        // Runs once everything this frame queued at a higher priority has run -
        // layout, render, the input that arrived meanwhile - so the time from the
        // frame starting to here is how long the UI thread was busy with it.
        var generation = _generation;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation == _generation)
                _worstBusyMs = Math.Max(_worstBusyMs, Stopwatch.GetElapsedTime(now).TotalMilliseconds);
        }, DispatcherPriority.Background);

        if (Stopwatch.GetElapsedTime(_secondStarted, now).TotalSeconds >= 1)
        {
            Report(Stopwatch.GetElapsedTime(_secondStarted, now).TotalSeconds);
            StartSecond(now);
        }

        RequestFrame();
    }

    private void StartSecond(long now)
    {
        _secondStarted = now;
        _frames = 0;
        _worstGapMs = 0;
        _missed = 0;
        _worstBusyMs = 0;
        _gen0 = GC.CollectionCount(0);
        _gen1 = GC.CollectionCount(1);
        _gen2 = GC.CollectionCount(2);
        _paused = GC.GetTotalPauseDuration();
    }

    private void Report(double seconds)
    {
        var paused = (GC.GetTotalPauseDuration() - _paused).TotalMilliseconds;
        var gc = $"GC {GC.CollectionCount(0) - _gen0}/{GC.CollectionCount(1) - _gen1}/{GC.CollectionCount(2) - _gen2}";
        if (paused > 0)
            gc += $" {paused:F1}ms";

        _text.Text =
            $"{_frames / seconds,3:F0} fps  worst {_worstGapMs,5:F1}ms  missed {_missed,3}\n" +
            $"UI max {_worstBusyMs,5:F1}ms  {gc}";
    }
}
