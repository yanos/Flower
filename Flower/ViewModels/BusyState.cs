using System;
using System.Threading;

using Avalonia.Threading;

namespace Flower.ViewModels;

// The status bar's spinner + message, as a standalone counter rather than
// fields on MainViewModel - extracted so the collaborators split out of that
// class (ITunesImportCoordinator, and anything else that runs a long
// operation) can drive the same one indicator without a back-reference to the
// ViewModel that happens to surface it. MainViewModel owns one of these and
// forwards Changed to its own IsBusy/BusyMessage PropertyChanged.
//
// Scopes nest: the innermost BeginScope call wins the message, and only the
// outermost one's disposal clears it (see App.axaml.cs's startup sequence,
// which holds one scope across the whole rescan + both iTunes syncs while each
// step opens its own more specific one inside it).
public sealed class BusyState
{
    private int _count;
    private string? _message;

    public bool    IsBusy  => _count > 0;
    public string? Message => _message;

    // Raised whenever IsBusy or Message changes. Always delivered on the UI
    // thread - see Notify below.
    public event EventHandler? Changed;

    // The count itself is bumped synchronously (needed immediately regardless
    // of caller thread, to correctly track overlapping scopes). The
    // notification used to always go through Dispatcher.UIThread.Post, even
    // when the caller was already on the UI thread (the common case - every
    // button-click command runs synchronously up to its first await) - a real
    // bug once SyncITunesPlayCountAsync started also calling this from a
    // background Task.Run (App.axaml.cs's startup rescan): IsBusy's IsVisible
    // binding "worked" anyway (something else happened to force a UI-thread
    // re-evaluation around the same time), but BusyMessage's TextBlock
    // silently never updated. Notify below fires immediately when already on
    // the UI thread instead of unconditionally deferring, so the spinner and
    // message show up as soon as this method returns rather than depending on
    // something else happening to pump the dispatcher queue first.
    public IDisposable BeginScope(string? message = null)
    {
        Interlocked.Increment(ref _count);
        Notify(message);
        return new Scope(this);
    }

    private void Notify(string? message)
    {
        void Raise()
        {
            _message = message;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Raise();
        else
            Dispatcher.UIThread.Post(Raise);
    }

    private sealed class Scope : IDisposable
    {
        private readonly BusyState _owner;
        internal Scope(BusyState owner) => _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Decrement(ref _owner._count) == 0)
                _owner.Notify(null);
        }
    }
}
