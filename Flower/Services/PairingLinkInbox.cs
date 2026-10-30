using System;
using System.Collections.Generic;

using Avalonia.Threading;

namespace Flower.Services;

// Where a flower:// pairing link opened from outside the app arrives, whichever
// way the platform hands it over: Avalonia's IActivatableLifetime on macOS, iOS
// and Android (App.axaml.cs), the command line on Windows and Linux
// (Flower.Desktop's Program), and a second launch forwarding to the first
// (PairingLinkForwarder). Whoever can act on one - the desktop window, the
// phone's Settings - attaches once it is ready to.
//
// It exists because the two ends are not ready at the same time. A link that
// launched the app arrives before there is a window to ask about it in, and
// one opened while the app runs arrives on whatever thread the platform chose.
// Links that come before a handler are held and handed over when it attaches,
// on the UI thread like every later one.
//
// Static, through Current, for the reason PeerHttpClient.IsPinnedServerKey is:
// the earliest sender is Program.Main, which runs before any container exists.
public sealed class PairingLinkInbox
{
    public static PairingLinkInbox Current { get; } = new();

    // The same link delivered twice inside this window is one link. Two of the
    // paths above can both see a single open - a cold start on Android carries
    // its link in the launch intent and in the activation event - and a second
    // confirmation for a code the first is already spending would only fail.
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(10);

    private readonly Action<Action> _post;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly List<PairingInvite> _held = [];
    private Action<PairingInvite>? _handler;
    private (string Link, DateTimeOffset At)? _last;

    public PairingLinkInbox(Action<Action>? post = null, TimeProvider? clock = null)
    {
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _clock = clock ?? TimeProvider.System;
    }

    // Whether the text was a pairing link at all. Anything else - a file path a
    // shell passed, an http URL - is not this inbox's to act on.
    public bool Deliver(string? text)
    {
        if (PairingInvite.TryParse(text) is not { } invite)
            return false;

        Action<PairingInvite>? handler;
        lock (_gate)
        {
            var link = invite.ToString();
            var now = _clock.GetUtcNow();
            if (_last is { } last && last.Link == link && now - last.At < RepeatWindow)
                return true;
            _last = (link, now);

            handler = _handler;
            if (handler == null)
            {
                _held.Add(invite);
                return true;
            }
        }

        _post(() => handler(invite));
        return true;
    }

    // The first argument that is a pairing link, as Windows and Linux pass one:
    // the registered command is the executable followed by the URL.
    public bool DeliverArguments(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (Deliver(arg))
                return true;
        }
        return false;
    }

    // One handler for the life of the app; attaching again replaces it.
    public void Attach(Action<PairingInvite> handler)
    {
        List<PairingInvite> held;
        lock (_gate)
        {
            _handler = handler;
            held = [.. _held];
            _held.Clear();
        }

        foreach (var invite in held)
            _post(() => handler(invite));
    }
}
