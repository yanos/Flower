using System;

using Avalonia.Threading;

namespace Flower.ViewModels;

// The phone's status pill: what the desktop's status tab says (BusyState), but
// only for work that has gone on long enough to be worth a word. The scan
// every launch starts reads a phone's few hundred songs in well under a
// second, and a pill popping up over the list and straight back down at every
// launch would be noise; a first import of a few thousand, or an album
// downloading, is minutes, and that is what this is for.
//
// Up once the work has gone on for ShowAfter, and down LingerFor after it
// ends - so its last word ("300 of 300 songs") is readable rather than gone
// the moment it is said. Work that starts again while it lingers keeps it up.
public sealed class DelayedBusyStatus : ViewModelBase, IDisposable
{
    public static readonly TimeSpan ShowAfter = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LingerFor = TimeSpan.FromSeconds(1);

    private readonly BusyState _busy;
    private readonly Func<TimeSpan, Action, IDisposable> _schedule;
    private IDisposable? _pending;

    // schedule runs the action once after the delay, on the UI thread, unless
    // the IDisposable it returns is disposed first. A DispatcherTimer unless
    // a test hands it something it can run by hand.
    public DelayedBusyStatus(BusyState busy, Func<TimeSpan, Action, IDisposable>? schedule = null)
    {
        _busy = busy;
        _schedule = schedule ?? OnDispatcherTimer;
        _busy.Changed += OnBusyChanged;
        OnBusyChanged(null, EventArgs.Empty);
    }

    private bool _isShown;
    public bool IsShown
    {
        get => _isShown;
        private set => SetProperty(ref _isShown, value);
    }

    // What was said last. Kept when the work ends and BusyState's message
    // goes to null, so the pill does not go blank for its last second.
    private string? _text;
    public string? Text
    {
        get => _text;
        private set => SetProperty(ref _text, value);
    }

    private void OnBusyChanged(object? sender, EventArgs e)
    {
        if (_busy.Message is { } message)
            Text = message;

        if (_busy.IsBusy)
        {
            if (IsShown)
                Cancel();
            else
                _pending ??= _schedule(ShowAfter, Settle);
        }
        else if (IsShown)
        {
            Cancel();
            _pending = _schedule(LingerFor, Settle);
        }
        else
        {
            Cancel();
        }
    }

    // Both timers end the same way: up if there is work going on by then, down
    // if not. Asked again rather than assumed, since the work may have ended or
    // come back while the timer ran.
    private void Settle()
    {
        _pending = null;
        IsShown = _busy.IsBusy;
    }

    private void Cancel()
    {
        _pending?.Dispose();
        _pending = null;
    }

    public void Dispose()
    {
        _busy.Changed -= OnBusyChanged;
        Cancel();
    }

    private static IDisposable OnDispatcherTimer(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
        return new StopTimer(timer);
    }

    private sealed class StopTimer(DispatcherTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stop();
    }
}
