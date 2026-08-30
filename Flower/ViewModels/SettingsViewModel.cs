using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Flower.Logging;
using Flower.Persistence;

using Microsoft.Extensions.Logging;

namespace Flower.ViewModels;

// Drives SettingsPanel, for either this device's settings or a remote Flower
// server's - the difference is entirely which ISettingsBackend it was handed.
//
// This is a *draft*: edits live here until Save, which is what finally makes
// Cancel mean something. The old SettingsWindow applied the alias, the theme and
// every checkbox the moment they were touched and only treated the library-folder
// list as cancellable, so "Cancel" quietly kept most of what had just been
// changed. Nothing about the remote case could work that way anyway - every write
// there is a request, and firing one per keystroke is not an option.
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsBackend _backend;
    private SettingsSnapshot _snapshot = new();
    private IReadOnlyList<string> _originalPaths = [];

    // False until LoadAsync has populated everything - the iTunes master switch
    // edits the pending paths list as a side effect, which must not happen while
    // merely restoring the persisted state.
    private bool _loaded;

    // appSettings/appSettingsStore are only the Logs tab's viewer preferences
    // (font size, minimum level, word wrap) and where to persist them - shared
    // with the app's own Log window, so the two read the way the same person
    // set them up. Defaulted because most callers have no Logs tab at all: the
    // local backend switches it off, and the tests construct this bare.
    public SettingsViewModel(
        ISettingsBackend backend,
        AppSettings? appSettings = null,
        AppSettingsStore? appSettingsStore = null,
        ILogger<SettingsViewModel>? logger = null)
    {
        _logger = logger;
        _backend = backend;
        Capabilities = backend.Capabilities;
        LogViewer = new LogViewerViewModel(appSettings ?? new AppSettings(), appSettingsStore);
    }

    // The Logs tab's viewer, the same one the app's Log window uses - see
    // LogViewerViewModel. What differs is only what gets loaded into it: there,
    // this device's live log; here, whichever row of LogSources is selected.
    public LogViewerViewModel LogViewer { get; }

    public ISettingsBackend Backend => _backend;
    public SettingsCapabilities Capabilities { get; }

    // The same tab holds two different things depending on who is being
    // configured (see SettingsPanel's Devices tab and RefreshDevicesTab): a
    // client picks the one server it pairs with, a server lists the devices it
    // has approved. Naming it "Devices" on a client was naming the wrong half -
    // a client has no device list at all.
    public string DevicesTabHeader => Capabilities.PairedServerPicker ? "Servers" : "Devices";

    public ObservableCollection<LibraryPathRow> LibraryPaths { get; } = [];
    public ObservableCollection<TrustedPeerRow> Devices { get; } = [];
    public ObservableCollection<DeniedPeerRow> DeniedDevices { get; } = [];
    public ObservableCollection<SubsonicCredentialRow> SubsonicCredentials { get; } = [];

    // The pending list, which the rows above are a rendering of. Kept separately
    // because the rows carry a song count that has to be recomputed whenever the
    // list changes, and because comparing against _originalPaths on save is a set
    // comparison over plain strings.
    private readonly List<string> _paths = [];

    public bool IsLoaded
    {
        get => _loaded;
        private set => SetProperty(ref _loaded, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    // Shown inline rather than raised as a dialog: every failure reachable from
    // this screen (not an admin, server went away, a folder that cannot be
    // written) is an ordinary outcome of clicking a button, and the panel is
    // rendered in a browser where there is no Window to own a dialog anyway.
    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private string _alias = "";
    public string Alias
    {
        get => _alias;
        set => SetProperty(ref _alias, value);
    }

    // Bound as a ComboBox index rather than the enum, so the XAML can stay a
    // plain three-item ComboBox with no converter or enum-source plumbing.
    private int _themeIndex;
    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetProperty(ref _themeIndex, value);
    }

    public AppThemePreference ThemePreference => ThemeIndex switch
    {
        1 => AppThemePreference.Light,
        2 => AppThemePreference.Dark,
        _ => AppThemePreference.System,
    };

    private bool _integrateWithITunes;
    public bool IntegrateWithITunes
    {
        get => _integrateWithITunes;
        set
        {
            if (!SetProperty(ref _integrateWithITunes, value))
                return;

            OnPropertyChanged(nameof(CanSyncFromITunes));
            ApplyAppleMusicFolder();
        }
    }

    // Whether the machine being configured - this device, or the server this page
    // administers - actually has an iTunes/Music.app library folder to integrate
    // with. All three switches are gated on it: they are real, remembered settings,
    // but with no folder found there is nothing for them to turn on, and a switch
    // that silently does nothing is worse than one that explains itself (see
    // ITunesUnavailableMessage, shown in its place).
    public bool HasAppleMusicFolder => _snapshot.AppleMusicFolder is { Length: > 0 };

    public bool CanIntegrateWithITunes => HasAppleMusicFolder && CanManageLibrary;

    // The two per-track imports only mean anything while the integration as a
    // whole is on. Disabled rather than unchecked, so each keeps whatever the user
    // had already chosen for when it is switched back on.
    public bool CanSyncFromITunes => IntegrateWithITunes && CanIntegrateWithITunes;

    public bool ShowsITunesUnavailable => Capabilities.ITunesIntegration && !HasAppleMusicFolder;

    public string ITunesUnavailableMessage =>
        "No iTunes/Music.app library folder could be found" +
        (Capabilities.ServerNetwork ? " on the server." : " on this device.");

    private bool _syncPlayCountFromITunes;
    public bool SyncPlayCountFromITunes
    {
        get => _syncPlayCountFromITunes;
        set => SetProperty(ref _syncPlayCountFromITunes, value);
    }

    private bool _syncDateAddedFromITunes;
    public bool SyncDateAddedFromITunes
    {
        get => _syncDateAddedFromITunes;
        set => SetProperty(ref _syncDateAddedFromITunes, value);
    }

    private bool _shareLogsWithPairedServer;
    public bool ShareLogsWithPairedServer
    {
        get => _shareLogsWithPairedServer;
        set => SetProperty(ref _shareLogsWithPairedServer, value);
    }

    private string _advertisedHost = "";
    public string AdvertisedHost
    {
        get => _advertisedHost;
        set => SetProperty(ref _advertisedHost, value);
    }

    private bool _advertiseOnLan = true;
    public bool AdvertiseOnLan
    {
        get => _advertiseOnLan;
        set => SetProperty(ref _advertiseOnLan, value);
    }

    private bool _trustTailscaleRange = true;
    public bool TrustTailscaleRange
    {
        get => _trustTailscaleRange;
        set => SetProperty(ref _trustTailscaleRange, value);
    }

    // Off by default and deliberately blunt about what it does: with this on,
    // the only thing between the library and the open internet is the signature
    // check on every route that matters. See FlowerServerOptions
    // .AllowPublicAccess, and docs/OPEN-INTERNET-REVIEW.md for the read-through
    // that decided this was survivable.
    private bool _allowPublicAccess;
    public bool AllowPublicAccess
    {
        get => _allowPublicAccess;
        set => SetProperty(ref _allowPublicAccess, value);
    }

    // One CIDR per line. A list control for four rarely-touched strings would be
    // more chrome than content, and this is a field an operator pastes into.
    private string _allowedCidrsText = "";
    public string AllowedCidrsText
    {
        get => _allowedCidrsText;
        set => SetProperty(ref _allowedCidrsText, value);
    }

    private string _pairingCode = "";
    public string PairingCode
    {
        get => _pairingCode;
        private set => SetProperty(ref _pairingCode, value);
    }

    private string _newCredentialLabel = "";
    public string NewCredentialLabel
    {
        get => _newCredentialLabel;
        set => SetProperty(ref _newCredentialLabel, value);
    }

    public string DataDirectory => _snapshot.DataDirectory;
    public string ITunesLibraryDescription => _snapshot.ITunesLibraryDescription;

    // Read-only, one row per origin. An operator deciding whether to open this
    // server to the internet, or what to type into a client that cannot discover
    // it, needs the address in front of them - it is not knowable from a browser
    // tab that reached the server through a proxy, or from a phone that has never
    // been on this LAN. A list rather than one newline-joined block because each
    // address is its own thing to click, copy and read (see SettingsPanel's
    // General tab), and a wrapped block of them all ran together.
    // Grouped by how the origin names the server (see ServerAddressKind), in a
    // fixed order rather than the order the server happened to report them: a
    // hostname works from anywhere and is the one to hand out, IPv4 is what the
    // rest of the house understands, IPv6 last. Empty groups are dropped.
    public IReadOnlyList<ServerAddressGroup> AddressGroups =>
        _snapshot.Addresses
            .Select(a => new ServerAddressRow(a))
            .GroupBy(r => r.Kind)
            .OrderBy(g => g.Key switch
            {
                ServerAddressKind.Hostname => 0,
                ServerAddressKind.IPv4 => 1,
                _ => 2,
            })
            .Select(g => new ServerAddressGroup(
                g.Key switch
                {
                    ServerAddressKind.Hostname => "By name",
                    ServerAddressKind.IPv4 => "IPv4",
                    _ => "IPv6",
                },
                g.ToList()))
            .ToList();
    public bool HasAddresses => _snapshot.Addresses.Count > 0;

    // Sits under the switch that opens this server to the internet, because it
    // is what that switch is about: the addresses listed on the General tab are
    // all inside-the-house ones, and none of them is what a phone off the LAN
    // would dial. Read-only, and absent rather than apologetic when there is no
    // answer - see SettingsSnapshot.PublicAddress.
    // The bare address, with the sentence around it left to the view - it reads
    // as prose plus one value to copy, and gluing them together would make the
    // whole line monospace or the address unselectable on its own.
    public string PublicAddressDisplay => _snapshot.PublicAddress ?? "";

    public bool HasPublicAddress => _snapshot.PublicAddress is { Length: > 0 };

    public string VersionDisplay => _snapshot.Version is { Length: > 0 } version ? $"Version {version}" : "";

    // See SettingsSnapshot.IsPairedToServer. A server always manages its own
    // library; a device that pairs to one stops managing its own the moment it
    // does, because from then on the library is synced in rather than curated.
    public bool CanManageLibrary =>
        !Capabilities.PairedServerPicker || !_snapshot.IsPairedToServer;

    public bool ShowsDeniedDevices => DeniedDevices.Count > 0;
    public bool HasDevices => Devices.Count > 0;

    // Raised when the Devices tab's content should be rebuilt.
    public event EventHandler? DeviceListChanged;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            _snapshot = await _backend.LoadAsync(ct);

            Alias = _snapshot.Alias;
            ThemeIndex = _snapshot.ThemePreference switch
            {
                AppThemePreference.Light => 1,
                AppThemePreference.Dark => 2,
                _ => 0,
            };
            _integrateWithITunes = _snapshot.IntegrateWithITunes;
            OnPropertyChanged(nameof(IntegrateWithITunes));
            SyncPlayCountFromITunes = _snapshot.SyncPlayCountFromITunes;
            SyncDateAddedFromITunes = _snapshot.SyncDateAddedFromITunes;
            ShareLogsWithPairedServer = _snapshot.ShareLogsWithPairedServer;
            AdvertisedHost = _snapshot.AdvertisedHost;
            AdvertiseOnLan = _snapshot.AdvertiseOnLan;
            TrustTailscaleRange = _snapshot.TrustTailscaleRange;
            AllowPublicAccess = _snapshot.AllowPublicAccess;
            AllowedCidrsText = string.Join(Environment.NewLine, _snapshot.AllowedCidrs);

            _paths.Clear();
            _paths.AddRange(_snapshot.LibraryPaths);
            _originalPaths = _snapshot.LibraryPaths.ToList();
            RefreshPathRows();

            OnPropertyChanged(nameof(DataDirectory));
            OnPropertyChanged(nameof(AddressGroups));
            OnPropertyChanged(nameof(HasAddresses));
            OnPropertyChanged(nameof(PublicAddressDisplay));
            OnPropertyChanged(nameof(HasPublicAddress));
            OnPropertyChanged(nameof(ITunesLibraryDescription));
            OnPropertyChanged(nameof(VersionDisplay));
            OnPropertyChanged(nameof(CanManageLibrary));
            OnPropertyChanged(nameof(HasAppleMusicFolder));
            OnPropertyChanged(nameof(CanIntegrateWithITunes));
            OnPropertyChanged(nameof(CanSyncFromITunes));
            OnPropertyChanged(nameof(ShowsITunesUnavailable));
            OnPropertyChanged(nameof(ITunesUnavailableMessage));

            // Only ask a backend that actually has a roster. LocalSettingsBackend
            // throws NotSupportedException from LoadDevicesAsync (a client
            // approves nobody), and that throw used to be caught below as a
            // failure - painting the red error line under every tab of this
            // device's own settings, and leaving IsLoaded false so the iTunes
            // switch's ApplyAppleMusicFolder side effect never armed.
            if (Capabilities.TrustedDevices)
                await RefreshDevicesAsync(ct);
            if (Capabilities.SubsonicCredentials)
                await RefreshSubsonicCredentialsAsync(ct);

            IsLoaded = true;
            DeviceListChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            // Reordering never happens (Add always appends, Remove just removes),
            // so a set comparison against what this screen opened with is enough
            // to answer "does this need a rescan".
            var pathsChanged = !_paths.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(_originalPaths);

            StatusMessage = await _backend.SaveAsync(new SettingsDraft
            {
                Alias = Alias,
                ThemePreference = ThemePreference,
                LibraryPaths = _paths.ToList(),
                LibraryPathsChanged = pathsChanged,
                IntegrateWithITunes = IntegrateWithITunes,
                SyncPlayCountFromITunes = SyncPlayCountFromITunes,
                SyncDateAddedFromITunes = SyncDateAddedFromITunes,
                ShareLogsWithPairedServer = ShareLogsWithPairedServer,
                AdvertisedHost = AdvertisedHost,
                AdvertiseOnLan = AdvertiseOnLan,
                TrustTailscaleRange = TrustTailscaleRange,
                AllowPublicAccess = AllowPublicAccess,
                AllowedCidrs = ParseCidrs(),
            }, ct);

            _originalPaths = _paths.ToList();
            return true;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddLibraryPath(string path)
    {
        if (_paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;

        _paths.Add(path);
        RefreshPathRows();
    }

    [RelayCommand]
    private void RemoveLibraryPath(LibraryPathRow? row)
    {
        if (row == null)
            return;

        _paths.RemoveAll(p => string.Equals(p, row.Path, StringComparison.OrdinalIgnoreCase));
        RefreshPathRows();
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        await RunAsync(async ct =>
        {
            await _backend.RescanAsync(ct);
            StatusMessage = "Scanning the library folders.";
        });
    }

    [RelayCommand]
    private Task RebuildDatabaseAsync() => RunAsync(ct => _backend.RebuildDatabaseAsync(ct));

    // What the next code will grant, ticked beside the one button that issues
    // them. Two buttons ("Add Device…"/"Add Administrator…") used to say this
    // instead, which made the rarer, more dangerous one just as easy to press
    // by accident as the ordinary one. Not remembered between presses, for the
    // same reason MainViewModel.PairingCodeGrantsAdmin isn't.
    private bool _pairingCodeGrantsAdmin;
    public bool PairingCodeGrantsAdmin
    {
        get => _pairingCodeGrantsAdmin;
        set => SetProperty(ref _pairingCodeGrantsAdmin, value);
    }

    [RelayCommand]
    private Task IssuePairingCodeAsync() => RunAsync(async ct =>
    {
        var grantsAdmin = PairingCodeGrantsAdmin;
        PairingCode = await _backend.IssuePairingCodeAsync(grantsAdmin, ct);
        StatusMessage = grantsAdmin
            ? "This code grants administrator access. Enter it on the device you are adding; it expires in a few minutes."
            : "Enter this code on the device you are adding. It expires in a few minutes.";
    });

    [RelayCommand]
    private Task ForgetDeviceAsync(TrustedPeerRow? device) => RunAsync(async ct =>
    {
        if (device == null)
            return;

        await _backend.ForgetDeviceAsync(device, ct);
        await RefreshDevicesAsync(ct);
    });

    [RelayCommand]
    private Task ForgetDenialAsync(DeniedPeerRow? device) => RunAsync(async ct =>
    {
        if (device == null)
            return;

        await _backend.ForgetDenialAsync(device, ct);
        await RefreshDevicesAsync(ct);
    });

    [RelayCommand]
    private Task IssueSubsonicCredentialAsync() => RunAsync(async ct =>
    {
        var label = string.IsNullOrWhiteSpace(NewCredentialLabel) ? "Subsonic client" : NewCredentialLabel.Trim();
        var issued = await _backend.IssueSubsonicCredentialAsync(label, ct);
        NewCredentialLabel = "";

        await RefreshSubsonicCredentialsAsync(ct);
        // The listing endpoint never returns a password, so the only copy that
        // will ever exist is the one in this response - put that row back at the
        // top of the list with it still attached.
        var listed = SubsonicCredentials.FirstOrDefault(c => c.Username == issued.Username);
        if (listed != null)
            SubsonicCredentials.Remove(listed);
        SubsonicCredentials.Insert(0, issued);

        StatusMessage = "Copy this password now - it is not stored and cannot be shown again.";
    });

    [RelayCommand]
    private Task RevokeSubsonicCredentialAsync(SubsonicCredentialRow? credential) => RunAsync(async ct =>
    {
        if (credential == null)
            return;

        await _backend.RevokeSubsonicCredentialAsync(credential, ct);
        await RefreshSubsonicCredentialsAsync(ct);
    });

    // Which logs the Logs tab can show: the server's own, then one row per
    // device on its roster. Rebuilt from that roster whenever it is loaded, so
    // the two cannot disagree about who exists.
    public ObservableCollection<LogSourceRow> LogSources { get; } = [];

    private LogSourceRow? _selectedLogSource;
    public LogSourceRow? SelectedLogSource
    {
        get => _selectedLogSource;
        set
        {
            if (!SetProperty(ref _selectedLogSource, value))
                return;
            _ = RefreshLogAsync();
        }
    }

    // Guards against a slow fetch for a row the user has already moved away
    // from painting its lines under a different name - the same stale-response
    // problem PeerLibraryViewModel solves with its own _requestId, and the one
    // failure here that would actively mislead rather than merely annoy.
    private int _logRequestId;

    // A busy client can produce tens of thousands of diagnostic lines in the
    // seven days ClientLogStore retains. Ask for the whole practical window;
    // the server still applies this as a hard ceiling to the response.
    private const int LogLimit = 100_000;

    // How often the tab re-asks while it is the one on screen. A log is read to
    // watch something happen - starting a rescan, pairing a phone, chasing an
    // error that is being reproduced right now - and a pane that only moves when
    // the reader remembers to press Refresh is the wrong shape for that. Two
    // seconds is under the threshold where a line feels late, and the server's
    // own log is answered from memory as a delta (see LoadLogAsync's
    // afterSequence), so a tick that finds nothing costs one small 200.
    private static readonly TimeSpan LogPollInterval = TimeSpan.FromSeconds(2);

    // Only ticks while the Logs tab is actually showing - see SetLogTabActive.
    // Created lazily rather than in the constructor because a DispatcherTimer
    // needs a Dispatcher, and most of the things that construct this ViewModel
    // (the local backend, the tests) never open a Logs tab at all.
    private DispatcherTimer? _logPollTimer;

    // Guards a slow tick against the next one landing on top of it.
    private bool _logPollInFlight;

    // Cursor into the server's own log numbering: what the viewer has already
    // been shown. Reset by a full read, advanced by every tick.
    private long _logSequence = InMemoryLogStore.BeforeFirstSequence;

    // How many entries the device snapshot on screen had. A device's log is not
    // sequenced - it is a merged file history the device re-pushes at the end of
    // each sync - so "has it changed" is answered by its size, and a change
    // re-renders the whole thing rather than appending to it.
    private int _deviceLogCount;

    // False while the pane is showing a placeholder rather than log lines, so a
    // tick that finally finds something replaces the sentence instead of
    // appending its first line underneath it.
    private bool _logHasContent;

    // Called by SettingsPanel as the Logs tab comes and goes (including the
    // panel itself being unloaded). Polling a server every two seconds for a
    // pane nobody is looking at would be pure waste, and on a browser tab
    // administering a remote server it would also be the only traffic left once
    // the reader moved on.
    public void SetLogTabActive(bool active)
    {
        if (!Capabilities.Log)
            return;

        if (!active)
        {
            _logPollTimer?.Stop();
            return;
        }

        if (_logPollTimer == null)
        {
            _logPollTimer = new DispatcherTimer { Interval = LogPollInterval };
            _logPollTimer.Tick += OnLogPollTick;
        }

        _logPollTimer.Start();
    }

    private async void OnLogPollTick(object? sender, EventArgs e)
    {
        if (_logPollInFlight)
            return;

        _logPollInFlight = true;
        try
        {
            await FollowLogAsync();
        }
        finally
        {
            _logPollInFlight = false;
        }
    }

    // One poll: whatever has been logged since the last look, appended to what
    // is already on screen. Public because it is also the whole of what the
    // timer does, and a test that had to wait out real two-second ticks to
    // observe it would be testing DispatcherTimer rather than this.
    public Task FollowLogAsync() => ReadLogAsync(fromScratch: false);

    [RelayCommand]
    private Task RefreshLogAsync() => ReadLogAsync(fromScratch: true);

    // fromScratch is the difference between the reader asking (a new selection,
    // the Refresh button) and the poll asking: the first says "(loading...)",
    // starts the cursor over and repaints the pane whatever comes back, while
    // the second is silent about everything - including failures, which on a
    // two-second timer are far more likely to be one dropped request than
    // anything the reader needs a sentence about.
    private async Task ReadLogAsync(bool fromScratch)
    {
        var source = SelectedLogSource;

        if (fromScratch)
        {
            _logSequence = InMemoryLogStore.BeforeFirstSequence;
            _deviceLogCount = 0;
            _logHasContent = false;
            _logRequestId++;
        }

        var requestId = _logRequestId;

        if (source == null)
        {
            if (fromScratch)
                ShowLogPlaceholder("(nothing selected)");
            return;
        }

        if (fromScratch)
            ShowLogPlaceholder("(loading...)");

        // Deliberately not RunAsync: that owns the panel's busy flag and its
        // one error line, both of which belong to saving and to the roster.
        // Reading a log is a browse, and a failed one has its own pane to say
        // so in rather than a banner over the whole screen.
        try
        {
            if (source.Fingerprint == null)
                await ReadServerLogAsync(requestId);
            else
                await ReadDeviceLogAsync(source, requestId);
        }
        catch (Exception ex)
        {
            if (fromScratch && requestId == _logRequestId)
                ShowLogPlaceholder($"(could not read this log: {ex.Message})");
        }
    }

    private async Task ReadServerLogAsync(int requestId)
    {
        var slice = await _backend.LoadLogAsync(LogLimit, _logSequence);
        if (requestId != _logRequestId)
            return; // The selection moved on while this was in flight.

        _logSequence = slice.LastSequence;

        if (slice.Entries.Count == 0)
        {
            if (!_logHasContent)
                ShowLogPlaceholder("(the server has logged nothing yet)");
            return;
        }

        if (_logHasContent)
        {
            // The viewer coalesces a burst of these into one batch itself, so
            // handing it a slice a line at a time costs nothing extra.
            foreach (var entry in slice.Entries)
                LogViewer.Append(entry);
            return;
        }

        _logHasContent = true;
        LogViewer.ShowLog(slice.Entries);
    }

    private async Task ReadDeviceLogAsync(LogSourceRow source, int requestId)
    {
        var entries = await _backend.LoadDeviceLogAsync(source.Fingerprint!, LogLimit);
        if (requestId != _logRequestId)
            return;

        if (entries == null)
        {
            // Deliberately not the same sentence as an empty log: nothing has
            // arrived from this device yet, which is a different thing from
            // this device having been quiet, and only the first is worth
            // waiting on.
            if (!_logHasContent)
                ShowLogPlaceholder(
                    $"(no log snapshot received from \"{source.Name}\" yet - it sends one at the end of each sync, if log sharing is on there)");
            return;
        }

        if (entries.Count == 0)
        {
            if (!_logHasContent)
                ShowLogPlaceholder($"(\"{source.Name}\" pushed a log with nothing in it)");
            return;
        }

        // Unchanged since the last look: repainting would be invisible except
        // for throwing away the reader's scroll position and selection.
        if (_logHasContent && entries.Count == _deviceLogCount)
            return;

        _deviceLogCount = entries.Count;
        _logHasContent = true;
        LogViewer.ShowLog(entries);
    }

    private void ShowLogPlaceholder(string message)
    {
        _logHasContent = false;
        LogViewer.ShowPlaceholder(message);
    }

    public async Task RefreshDevicesAsync(CancellationToken ct = default)
    {
        Devices.Clear();
        foreach (var device in await _backend.LoadDevicesAsync(ct))
            Devices.Add(device);

        DeniedDevices.Clear();
        foreach (var device in await _backend.LoadDeniedDevicesAsync(ct))
            DeniedDevices.Add(device);

        // Keep the Logs tab's list in step with the roster it names. The
        // selection goes back to the server itself rather than following
        // whichever device happens to now sit where the old one did.
        LogSources.Clear();
        LogSources.Add(new LogSourceRow("This server", null));
        foreach (var device in Devices)
            LogSources.Add(new LogSourceRow(device.Alias, device.Fingerprint));
        SelectedLogSource = LogSources[0];

        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(ShowsDeniedDevices));
    }

    private async Task RefreshSubsonicCredentialsAsync(CancellationToken ct = default)
    {
        SubsonicCredentials.Clear();
        foreach (var credential in await _backend.LoadSubsonicCredentialsAsync(ct))
            SubsonicCredentials.Add(credential);
    }

    // Turning the iTunes integration on adds Music.app's media folder to the
    // pending paths list immediately, so it shows up in the folders list right
    // here instead of only appearing on some later launch. Editing the pending
    // list rather than saving directly keeps it cancellable and routes it
    // through the same "did the paths change" rescan as Add/Remove Folder.
    //
    // Turning it *off* deliberately leaves that folder where it is. It used to
    // remove it, which meant unchecking one box silently emptied the entire
    // library of anyone whose library is Music.app's folder and nothing else -
    // no folders to scan means no tracks (see Importer.Import), so 16k songs
    // disappeared as a side effect of declining two metadata imports. What this
    // switch governs is whether Flower reaches into Music.app at all: adopting
    // that folder in the first place, and the two per-track imports. A folder
    // already in the list is a folder the user has, and taking it back out is
    // the Remove Folder button's job, directly below it. Off is still what makes
    // such a removal stick - see ITunesIntegration.ResolveMediaFolderToAdopt,
    // which re-offers the folder on every load for as long as this is on.
    private void ApplyAppleMusicFolder()
    {
        if (!IsLoaded || !IntegrateWithITunes || _snapshot.AppleMusicFolder is not { } folder)
            return;

        if (_paths.Contains(folder, StringComparer.OrdinalIgnoreCase))
            return;

        _paths.Add(folder);
        RefreshPathRows();
    }

    private void RefreshPathRows()
    {
        LibraryPaths.Clear();
        foreach (var path in _paths)
            LibraryPaths.Add(new LibraryPathRow(path, _backend.CountSongsUnder(path)));
    }

    private List<string> ParseCidrs() =>
        AllowedCidrsText
            .Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken ct = default)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            await action(ct);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private readonly ILogger<SettingsViewModel>? _logger;

    // The server's own words where it gave any (ServerAdminException carries the
    // {"error": ...} body), the exception's otherwise.
    //
    // Logged as well as shown: ErrorMessage is a label in a panel the user is
    // about to close, so every failure to read or write server settings used to
    // leave no trace at all once they did. The message alone is what the user
    // gets; the exception is what makes the failure diagnosable afterwards.
    private void Fail(Exception ex)
    {
        _logger?.LogWarning(ex, "A settings operation failed: {Message}", ex.Message);
        ErrorMessage = ex.Message;
    }
}
