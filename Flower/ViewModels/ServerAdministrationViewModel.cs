using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Persistence;
using Flower.Services;

namespace Flower.ViewModels;

// Administering the server selected in desktop's sidebar: its settings, shown
// in place in the device-detail pane, and "Open in Browser" for its own page.
// Its own class rather than a region of MainViewModel
// (docs/CODE-REVIEW-2026-09.md E3). What it needs of the shell is which
// server, if any, may be administered right now (Show) - the shell knows the
// sidebar selection and the pairing state that decide it - and a way to sign
// as this device.
public sealed class ServerAdministrationViewModel : ViewModelBase
{
    private readonly Func<HttpClient, DiscoveredDevice, ServerAdminClient> _clientFor;
    private readonly AppSettings _appSettings;
    private readonly AppSettingsStore? _appSettingsStore;
    private readonly ILogger _logger;

    public ServerAdministrationViewModel(
        Func<HttpClient, DiscoveredDevice, ServerAdminClient> clientFor,
        AppSettings appSettings, AppSettingsStore? appSettingsStore, ILogger logger)
    {
        _clientFor = clientFor;
        _appSettings = appSettings;
        _appSettingsStore = appSettingsStore;
        _logger = logger;
    }

    // ── The settings panel ────────────────────────────────────────────────

    // The selected server's own settings, administered in place: the same
    // SettingsPanel its browser UI serves, over a RemoteServerSettingsBackend
    // signed as this device. Null while there is nothing to administer - no
    // server row selected, or one that has not approved this device yet.
    //
    // This is what the device-detail pane shows instead of what used to be
    // there, a browse view of that server's catalog. That view was the same
    // music twice: a paired server's tracks are merged into Library by
    // LibrarySyncService and stream from the ordinary track list through
    // IStreamUrlResolver, so the one thing about a server that had nowhere
    // else to live was its settings.
    private SettingsViewModel? _settings;
    public SettingsViewModel? Settings
    {
        get => _settings;
        private set
        {
            if (ReferenceEquals(_settings, value))
                return;
            _settings = value;
            OnPropertyChanged();
        }
    }

    // One client for every settings panel this window ever shows, rather than
    // one per selection: a SettingsViewModel outlives the call that created it
    // (it saves, reloads and tails a log on demand), so the disposable-per-call
    // shape the one-shot admin calls use does not work here.
    private HttpClient? _adminHttp;

    // Which server the panel currently on screen was built against - both
    // halves matter: a different server obviously needs a new panel, and so
    // does the same server reached at a new address (a LAN-to-tailnet handover
    // moves BaseUri under a fingerprint that never changes).
    private (string Fingerprint, Uri BaseUri)? _shownFor;

    // Every server settings screen built this session, kept rather than
    // rebuilt. Selecting a playlist and coming back used to throw the whole
    // thing away and reassemble it from nothing - a fresh load, a fresh roster,
    // the Logs tab back on the server's own log at the top - when the only
    // thing that had actually happened was the reader looking at something else
    // for a moment. Keeping it means the tab, the log being read, its scroll
    // position and any half-finished edit are simply still there, because they
    // were never taken apart.
    //
    // Keyed by fingerprint, with the address it was built against alongside: a
    // server reached at a new one (a LAN-to-tailnet handover) does need a new
    // screen, since that address is baked into the admin client behind it.
    // Bounded by how many servers the sidebar has ever shown, which under this
    // app's deployment model is one and change.
    private readonly Dictionary<string, (Uri BaseUri, SettingsViewModel Settings)> _kept = new();

    // The server whose settings to show now, or null for none. Called both
    // when the selection moves and whenever the pair-button state changes,
    // since pairing with the selected server - or its approval landing a
    // moment later - is what turns an unadministerable row into one whose
    // settings can be read at all.
    public void Show(DiscoveredDevice? device)
    {
        var target = device == null ? ((string, Uri)?)null : (device.Fingerprint, device.BaseUri);
        if (target == _shownFor)
            return;

        _shownFor = target;
        Settings = device == null ? null : SettingsFor(device);
    }

    private SettingsViewModel SettingsFor(DiscoveredDevice device)
    {
        if (_kept.TryGetValue(device.Fingerprint, out var kept))
        {
            if (kept.BaseUri == device.BaseUri)
                return kept.Settings;

            // Reached somewhere else now, so the screen that was talking to the
            // old address is no longer talking to anything.
            _kept.Remove(device.Fingerprint);
        }

        _adminHttp ??= PeerHttpClient.Create(TimeSpan.FromSeconds(15));
        var settings = new SettingsViewModel(
            new RemoteServerSettingsBackend(_clientFor(_adminHttp, device)),
            _appSettings, _appSettingsStore);
        _kept[device.Fingerprint] = (device.BaseUri, settings);
        return settings;
    }

    // ── Open in Browser ───────────────────────────────────────────────────

    // Set when minting a browser session against the server failed - most
    // usefully when this device is paired but not an administrator, which is
    // an ordinary outcome and so is said inline next to the button rather than
    // raised as a dialog. Same treatment as PairingCodeError.
    private string? _openError;
    public string? OpenError
    {
        get => _openError;
        private set => SetProperty(ref _openError, value);
    }

    // An error is about one server: the selection moving on takes it away.
    public void ForgetOpenError() => OpenError = null;

    private bool _isOpening;
    public bool IsOpening
    {
        get => _isOpening;
        private set => SetProperty(ref _isOpening, value);
    }

    // Opens the server's own settings page in the OS browser.
    //
    // A browser tab holds its own WebCrypto keypair and pairs like any other
    // device (see BrowserPeerCredentials), so what this hands over is not a
    // credential for the tab to *use* but a one-time, admin-granting pairing
    // code for it to spend on becoming a device. In the URL fragment rather than
    // the query string because a fragment is never sent to the server as part of
    // the request, so the code cannot end up in an access log on the way in.
    //
    // Opening this a second time for a tab that already paired issues a code
    // nothing needs, which the tab's redeem attempt spends harmlessly. That is
    // cheap enough not to be worth a round-trip to find out first, and it is
    // also the way back in after site data is cleared.
    public async Task OpenInBrowserAsync(DiscoveredDevice device)
    {
        OpenError = null;
        IsOpening = true;
        try
        {
            using var http = PeerHttpClient.Create(TimeSpan.FromSeconds(15));
            var client = _clientFor(http, device);

            var pairing = await client.IssuePairingCodeAsync(grantsAdmin: true);
            if (!PlatformShell.OpenUrl(pairing.BrowserUrl))
                OpenError = "Could not open a browser.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open the settings page for {Device}", device.BaseUri);
            OpenError = ex is ServerAdminException ? ex.Message : "Could not reach that server.";
        }
        finally
        {
            IsOpening = false;
        }
    }
}
