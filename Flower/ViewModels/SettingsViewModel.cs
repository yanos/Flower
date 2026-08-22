using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using CommunityToolkit.Mvvm.Input;

using Flower.Persistence;

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

    public SettingsViewModel(ISettingsBackend backend)
    {
        _backend = backend;
        Capabilities = backend.Capabilities;
    }

    public ISettingsBackend Backend => _backend;
    public SettingsCapabilities Capabilities { get; }

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

    private bool _isServer;
    public bool IsServer
    {
        get => _isServer;
        set
        {
            if (!SetProperty(ref _isServer, value))
                return;

            OnPropertyChanged(nameof(CanManageLibrary));
            OnPropertyChanged(nameof(CanIntegrateWithITunes));
            OnPropertyChanged(nameof(CanSyncFromITunes));
            DeviceListChanged?.Invoke(this, EventArgs.Empty);
        }
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

    private string _logText = "";
    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    private string _newCredentialLabel = "";
    public string NewCredentialLabel
    {
        get => _newCredentialLabel;
        set => SetProperty(ref _newCredentialLabel, value);
    }

    public string DataDirectory => _snapshot.DataDirectory;
    public string ITunesLibraryDescription => _snapshot.ITunesLibraryDescription;
    public string VersionDisplay => _snapshot.Version is { Length: > 0 } version ? $"Version {version}" : "";

    // See SettingsSnapshot.IsPairedToServer: combined with the *draft* role, so
    // ticking "Act as Server" re-enables the library controls straight away
    // instead of only after saving and reopening.
    public bool CanManageLibrary =>
        !Capabilities.SyncRole || IsServer || !_snapshot.IsPairedToServer;

    public bool ShowsDeniedDevices => DeniedDevices.Count > 0;
    public bool HasDevices => Devices.Count > 0;

    // Raised when the Devices tab's content should be rebuilt - the panel decides
    // between a trusted-device list and the server picker from the live role, and
    // the role is editable right there on the General tab.
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
            _isServer = _snapshot.IsServer;
            OnPropertyChanged(nameof(IsServer));
            ShareLogsWithPairedServer = _snapshot.ShareLogsWithPairedServer;
            AdvertisedHost = _snapshot.AdvertisedHost;
            AdvertiseOnLan = _snapshot.AdvertiseOnLan;
            TrustTailscaleRange = _snapshot.TrustTailscaleRange;
            AllowedCidrsText = string.Join(Environment.NewLine, _snapshot.AllowedCidrs);

            _paths.Clear();
            _paths.AddRange(_snapshot.LibraryPaths);
            _originalPaths = _snapshot.LibraryPaths.ToList();
            RefreshPathRows();

            OnPropertyChanged(nameof(DataDirectory));
            OnPropertyChanged(nameof(ITunesLibraryDescription));
            OnPropertyChanged(nameof(VersionDisplay));
            OnPropertyChanged(nameof(CanManageLibrary));
            OnPropertyChanged(nameof(HasAppleMusicFolder));
            OnPropertyChanged(nameof(CanIntegrateWithITunes));
            OnPropertyChanged(nameof(CanSyncFromITunes));
            OnPropertyChanged(nameof(ShowsITunesUnavailable));
            OnPropertyChanged(nameof(ITunesUnavailableMessage));

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
                IsServer = IsServer,
                ShareLogsWithPairedServer = ShareLogsWithPairedServer,
                AdvertisedHost = AdvertisedHost,
                AdvertiseOnLan = AdvertiseOnLan,
                TrustTailscaleRange = TrustTailscaleRange,
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

    [RelayCommand]
    private Task IssuePairingCodeAsync() => RunAsync(async ct =>
    {
        PairingCode = await _backend.IssuePairingCodeAsync(grantsAdmin: false, ct);
        StatusMessage = "Enter this code on the device you are adding. It expires in a few minutes.";
    });

    [RelayCommand]
    private Task IssueAdminPairingCodeAsync() => RunAsync(async ct =>
    {
        PairingCode = await _backend.IssuePairingCodeAsync(grantsAdmin: true, ct);
        StatusMessage = "This code grants administrator access. It expires in a few minutes.";
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

    public Task RenameDeviceAsync(TrustedPeerRow device) => RunAsync(async ct =>
    {
        await _backend.RenameDeviceAsync(device, ct);
        // Re-derives the displayed value from scratch - in particular, an emptied
        // field falls back to the alias the peer reported for itself rather than
        // being left showing blank text.
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

    [RelayCommand]
    private Task RefreshLogAsync() => RunAsync(async ct =>
    {
        var lines = await _backend.LoadLogAsync(500, ct);
        LogText = lines.Count == 0 ? "(the server has logged nothing yet)" : string.Join(Environment.NewLine, lines);
    });

    public async Task RefreshDevicesAsync(CancellationToken ct = default)
    {
        Devices.Clear();
        foreach (var device in await _backend.LoadDevicesAsync(ct))
            Devices.Add(device);

        DeniedDevices.Clear();
        foreach (var device in await _backend.LoadDeniedDevicesAsync(ct))
            DeniedDevices.Add(device);

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

    // The server's own words where it gave any (ServerAdminException carries the
    // {"error": ...} body), the exception's otherwise.
    private void Fail(Exception ex) => ErrorMessage = ex.Message;
}
