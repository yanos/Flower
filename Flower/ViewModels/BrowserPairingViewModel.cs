using System;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

namespace Flower.ViewModels;

// A browser tab is a device like any other: until the server knows its key,
// every request it makes is refused. Such a tab shows nothing but a pairing
// screen - a box for a code, and where to get one - because everything else on
// the page could only ever be empty or refused. Set at startup by
// App.ReportBrowserPairing, from the server's own answer on the /info
// handshake, and left as it is on every other head, where nothing sets it.
//
// Its own class rather than a region of MainViewModel
// (docs/CODE-REVIEW-2026-09.md E3): the screen it drives is one page of the
// browser head, and nothing else in MainViewModel reads any of it.
public sealed class BrowserPairingViewModel : ViewModelBase
{
    // Where a code comes from - shown under the box. One string, so the
    // screen and the tests read the same words.
    public const string Help =
        "Pairing lets this browser play music from this server. You need a pairing code, which an administrator "
        + "of the server creates:\n\n"
        + "  •  In the Flower app on a computer: select the server in the sidebar and press Generate Pairing Code.\n"
        + "  •  In the Flower app on a phone: Settings, then Generate Pairing Code.\n"
        + "  •  In a browser that is already paired as an administrator: Server Settings, Devices, Generate Pairing Code.\n\n"
        + "Tick Administrator when creating it if this browser should also be able to change the server's settings. "
        + "Without it, this browser can play music but won't see the settings.\n\n"
        + "Nobody can create one? The server prints an administrator code in its log when it starts with no "
        + "administrator, or when it is started with --pairing-code (for Docker: docker compose logs flower). "
        + "A code works once, for ten minutes.";

    // The server said it does not know this tab.
    private bool _isUnpaired;
    public bool IsUnpaired
    {
        get => _isUnpaired;
        set
        {
            if (!SetProperty(ref _isUnpaired, value))
                return;
            RaiseScreenChanged();
        }
    }

    // Why this tab cannot pair at all - a page that cannot hold a key (plain
    // http from another machine, storage blocked). A code would not help, so
    // the screen says this instead of offering the box.
    private string? _cannotPairReason;
    public string? CannotPairReason
    {
        get => _cannotPairReason;
        set
        {
            if (!SetProperty(ref _cannotPairReason, value))
                return;
            RaiseScreenChanged();
        }
    }

    // Until the server has answered, the tab does not know which of the two
    // pages it is, so it shows neither - an opaque blank rather than a flash
    // of the library before the pairing screen covers it.
    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        set
        {
            if (!SetProperty(ref _isChecking, value))
                return;
            OnPropertyChanged(nameof(CoversPage));
        }
    }

    public bool ShowsScreen => IsUnpaired || CannotPairReason != null;
    public bool CoversPage => IsChecking || ShowsScreen;
    public bool CanEnterCode => IsUnpaired && CannotPairReason == null;

    private void RaiseScreenChanged()
    {
        OnPropertyChanged(nameof(ShowsScreen));
        OnPropertyChanged(nameof(CoversPage));
        OnPropertyChanged(nameof(CanEnterCode));
    }

    private string _code = "";
    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value);
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    private bool _isPairing;
    public bool IsPairing
    {
        get => _isPairing;
        private set => SetProperty(ref _isPairing, value);
    }

    // What spends a code, and what happens once one is spent - the browser
    // head's BrowserPeerCredentials.PairAsync and a page reload, handed in by
    // App so this view model neither knows about WebCrypto nor calls into
    // JavaScript. The pairer answers null for success, or the sentence to show.
    public Func<string, Task<string?>>? Pairer { get; set; }
    public Action? AfterPaired { get; set; }

    private IAsyncRelayCommand? _pairCommand;
    public IAsyncRelayCommand PairCommand => _pairCommand ??= new AsyncRelayCommand(PairAsync);

    private async Task PairAsync()
    {
        if (Pairer is not { } pair || IsPairing)
            return;

        IsPairing = true;
        Error = null;
        try
        {
            if (await pair(Code) is { } error)
            {
                Error = error;
                return;
            }

            // Everything the page built at startup was built for a tab the
            // server did not know. Starting over is how all of it asks again.
            AfterPaired?.Invoke();
        }
        finally
        {
            IsPairing = false;
        }
    }
}
