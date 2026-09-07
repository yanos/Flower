using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Flower.Controls;

// What one paired server's standing looks like, everywhere it is shown: the
// sidebar row, the device pane's header, the desktop server picker, and
// mobile's settings sheet.
//
// It is a control because those four had drifted apart while all meaning the
// same thing. Three hardcoded #4CAF50 and one AppSuccessBrush, so the check was
// a different green in the sidebar than three inches away in the pane beside
// it; four sizes between 11 and 16; and the spinning style declared once per
// host file.
//
// The defect that prompted it is the arrangement rather than the styling. Three
// of the four put the check and the spinner side by side, so a server that was
// syncing or waiting to be approved showed a lit check with a spinner turning
// next to it - claiming connected and connecting at once, which is not a state
// a server can be in. Here they are one glyph in one cell: Busy REPLACES
// Connected, and cannot be shown beside it, because the state is resolved to a
// single value before anything is made visible.
//
// Precedence, in Resolve below: busy beats unreachable beats connected. Busy
// leads because it is the only state that is about right now - the other two
// describe what was last found to be true, and a server being polled has
// already superseded them.
//
// Busy is also held for a minimum length once shown (MinimumBusyDuration). A
// sync of a library that has not changed finishes in well under the frame or
// two it takes to notice a glyph, so without the hold the spinner is a flicker
// in the corner of the eye - and a flicker reads as a fault, not as work
// happening. The hold costs a second of staleness on a state that is about to
// be superseded anyway.
public partial class ConnectionStatusIcon : UserControl
{
    public enum ConnectionState
    {
        // Nothing to say: not a paired server, or one this row knows nothing
        // about yet. The control takes up no space at all.
        None,
        Connected,
        Busy,
        Unreachable,
    }

    // 14 rather than the 11-to-16 spread the four call sites had grown. The
    // small end was the sidebar's, where an 11px check next to 13px text read
    // as a smudge; this is legible at a glance without becoming a second
    // bullet point beside the name.
    public const double DefaultIconSize = 14;

    public static readonly StyledProperty<bool> IsConnectedProperty =
        AvaloniaProperty.Register<ConnectionStatusIcon, bool>(nameof(IsConnected));

    // Both kinds of "working on it" arrive here: a pairing sent but not yet
    // approved, and a sync in flight. They are one state to a reader - the
    // server is being talked to and the answer is not in yet - and giving them
    // separate glyphs is what left two of them able to show at once.
    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<ConnectionStatusIcon, bool>(nameof(IsBusy));

    public static readonly StyledProperty<bool> IsUnreachableProperty =
        AvaloniaProperty.Register<ConnectionStatusIcon, bool>(nameof(IsUnreachable));

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<ConnectionStatusIcon, double>(nameof(IconSize), DefaultIconSize);

    public bool IsConnected
    {
        get => GetValue(IsConnectedProperty);
        set => SetValue(IsConnectedProperty, value);
    }

    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    public bool IsUnreachable
    {
        get => GetValue(IsUnreachableProperty);
        set => SetValue(IsUnreachableProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    // The one rule, as a pure function so a test can hold it without a visual
    // tree - which is the point of having written it down once.
    public static ConnectionState Resolve(bool connected, bool busy, bool unreachable) =>
        busy ? ConnectionState.Busy
        : unreachable ? ConnectionState.Unreachable
        : connected ? ConnectionState.Connected
        : ConnectionState.None;

    // Long enough to register as a thing that happened. Below about half a
    // second a spinner that appears and vanishes is indistinguishable from a
    // rendering glitch; a whole second also gives the rotation time to travel
    // far enough to read as a rotation.
    public static readonly TimeSpan DefaultMinimumBusyDuration = TimeSpan.FromSeconds(1);

    // Instance rather than a constant so a test can shorten it, and so a call
    // site with a genuinely different rhythm could too. Nothing does yet.
    public TimeSpan MinimumBusyDuration { get; set; } = DefaultMinimumBusyDuration;

    // How the hold is released. Swapped in tests, which then release it by
    // hand rather than waiting out a real second per case.
    internal Action<TimeSpan, Action>? ScheduleBusyRelease { get; set; }

    private bool _busyHeld;

    // Which hold the pending release belongs to. Busy going true again while
    // a release is in flight starts a fresh hold, and the older release must
    // not then cut it short.
    private int _busyGeneration;

    // What is actually shown: the state the caller reports, plus a busy that
    // has not yet been on screen long enough to have been seen.
    public ConnectionState State => Resolve(IsConnected, IsBusy || _busyHeld, IsUnreachable);

    public ConnectionStatusIcon()
    {
        InitializeComponent();
        Apply();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsBusyProperty && change.GetNewValue<bool>())
            BeginBusyHold();

        if (change.Property == IsConnectedProperty
            || change.Property == IsBusyProperty
            || change.Property == IsUnreachableProperty
            || change.Property == IconSizeProperty)
        {
            Apply();
        }
    }

    private void BeginBusyHold()
    {
        if (MinimumBusyDuration <= TimeSpan.Zero)
            return;

        _busyHeld = true;
        var generation = ++_busyGeneration;

        Schedule(MinimumBusyDuration, () =>
        {
            if (generation != _busyGeneration)
                return;

            _busyHeld = false;
            Apply();
        });
    }

    private void Schedule(TimeSpan delay, Action release)
    {
        if (ScheduleBusyRelease is { } scheduler)
        {
            scheduler(delay, release);
            return;
        }

        // A control that is not on screen has nothing to hold: the hold exists
        // so a state can be *seen*, and there is nobody to see this one. Worth
        // stating rather than starting the timer anyway, because a timer
        // outliving what started it is a real cost here - a sidebar row is a
        // recycled template instance, and Flower.Tests' TimerLeakGuard fails
        // any test that leaves one ticking on the shared dispatcher.
        if (TopLevel.GetTopLevel(this) == null)
        {
            release();
            return;
        }

        DispatcherTimer.RunOnce(release, delay);
    }

    private void Apply()
    {
        var state = State;

        ConnectedIcon.IsVisible = state == ConnectionState.Connected;
        BusyIcon.IsVisible = state == ConnectionState.Busy;
        UnreachableIcon.IsVisible = state == ConnectionState.Unreachable;

        // Collapsed rather than merely blank when there is nothing to report,
        // so a row with no server standing does not carry a hole where the
        // glyph would be. The Spacing of the StackPanel around it collapses
        // with it.
        IsVisible = state != ConnectionState.None;

        foreach (var icon in new Control[] { ConnectedIcon, BusyIcon, UnreachableIcon })
        {
            icon.Width = IconSize;
            icon.Height = IconSize;
        }
    }
}
