using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Importer;
using Flower.Persistence;

namespace Flower.ViewModels;

// This device's own settings, i.e. everything SettingsWindow used to do by
// reaching into MainViewModel from code-behind. Nothing here is new behaviour -
// it is the same property sets, the same stores and the same unawaited rescan,
// moved behind ISettingsBackend so the identical XAML can also be pointed at a
// remote server (see RemoteServerSettingsBackend).
public sealed class LocalSettingsBackend(MainViewModel viewModel) : ISettingsBackend
{
    public MainViewModel ViewModel { get; } = viewModel;

    public SettingsCapabilities Capabilities { get; } = new()
    {
        ThemePicker = true,
        ITunesIntegration = true,
        SyncRole = true,
        RevealAppDataLocation = true,
        RebuildDatabase = true,
        // The app has its own Log window (View > Log...), which does far more
        // than a flat tail - a Logs tab here would be a worse second copy.
        Log = false,
        // An app peer pairs by prompting its user to Allow, and has no Subsonic
        // credential store - both are a headless server's problem.
        PairingCodes = false,
        SubsonicCredentials = false,
        ServerNetwork = false,
    };

    public Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SettingsSnapshot
    {
        Alias = ViewModel.DeviceAlias,
        ThemePreference = ViewModel.ThemePreference,
        LibraryPaths = ViewModel.LibraryPaths.ToList(),
        IntegrateWithITunes = ViewModel.IntegrateWithITunes,
        SyncPlayCountFromITunes = ViewModel.SyncPlayCountFromITunes,
        SyncDateAddedFromITunes = ViewModel.SyncDateAddedFromITunes,
        IsServer = ViewModel.IsServer,
        ShareLogsWithPairedServer = ViewModel.ShareLogsWithPairedServer,
        ITunesLibraryDescription = DescribeITunesLibrarySource(),
        AppleMusicFolder = Flower.Importer.Importer.TryResolveAppleMusicFolder(),
        DataDirectory = AppDataDirectory.Path,
        Version = typeof(LocalSettingsBackend).Assembly.GetName().Version?.ToString(),
        IsPairedToServer = !string.IsNullOrEmpty(ViewModel.PairedServerFingerprint),
    });

    // The Library tab's *content* only stops making sense once this device is
    // actually pulling its library from a paired Server - a Server manages its own
    // library as always, and a Client that hasn't paired with anyone yet still has
    // (and can keep curating) its own local library right up until it actually
    // picks a server. So this is true whenever EITHER holds: acting as Server, or
    // a Client not currently paired to anyone.
    //
    // Read off the *live* role rather than the snapshot taken when the screen
    // opened, so flipping "Act as Server" re-enables the tab immediately - the
    // panel re-asks on every change.
    public bool CanManageLocalLibrary =>
        ViewModel.IsServer || string.IsNullOrEmpty(ViewModel.PairedServerFingerprint);

    // Deliberately applied property-by-property rather than through one settings
    // object: each of these setters already persists and already no-ops when the
    // value is unchanged (see MainViewModel), and several have side effects -
    // ThemePreference repaints, IsServer re-evaluates the sync role - that a bulk
    // write would have to reimplement.
    public async Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default)
    {
        // An empty alias is never persisted (it would show peers a blank name);
        // the field just keeps its last real value.
        if (!string.IsNullOrWhiteSpace(draft.Alias))
            ViewModel.DeviceAlias = draft.Alias.Trim();

        ViewModel.ThemePreference = draft.ThemePreference;
        ViewModel.IntegrateWithITunes = draft.IntegrateWithITunes;
        ViewModel.SyncPlayCountFromITunes = draft.SyncPlayCountFromITunes;
        ViewModel.SyncDateAddedFromITunes = draft.SyncDateAddedFromITunes;
        ViewModel.IsServer = draft.IsServer;
        ViewModel.ShareLogsWithPairedServer = draft.ShareLogsWithPairedServer;

        if (draft.LibraryPathsChanged)
            await ViewModel.SaveLibraryPathsAsync(draft.LibraryPaths.ToList());

        // Gated on CanManageLocalLibrary as well as each box's own value: a
        // disabled CheckBox still reports whatever it was set to before it went
        // disabled, so trusting IsChecked alone would let a paired Client kick off
        // an iTunes import it is not supposed to run.
        var syncPlayCount = CanManageLocalLibrary && draft.IntegrateWithITunes && draft.SyncPlayCountFromITunes;
        var syncDateAdded = CanManageLocalLibrary && draft.IntegrateWithITunes && draft.SyncDateAddedFromITunes;

        // All three run unawaited, on purpose: the screen closes as soon as the
        // (fast) settings write lands, and the (potentially long) rescan/import
        // shows progress on the now-visible MainView's busy spinner rather than
        // behind a still-modal window.
        if (draft.LibraryPathsChanged)
            _ = ViewModel.RescanLibraryAsync();
        if (syncPlayCount)
            _ = ViewModel.SyncITunesPlayCountAsync();
        if (syncDateAdded)
            _ = ViewModel.SyncITunesDateAddedAsync();

        return null;
    }

    public int CountSongsUnder(string folder)
    {
        var prefix = folder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        // Path is null for a sync placeholder track (a peer's catalog entry not
        // yet downloaded to this device - see LibraryDownloadService) - it can't
        // be "under" any local folder, so it just doesn't count, rather than
        // crashing the whole Settings screen open.
        return ViewModel.Library.Tracks.Count(
            t => t.Path?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
    }

    public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TrustedPeerRow>>(ViewModel.TrustedPeers.Load()
            .OrderByDescending(p => p.ApprovedAt)
            // A local nickname (see DeviceNicknameStore - also editable from the
            // sidebar's "Rename Device" context menu) wins over the alias the peer
            // reported when it was first approved.
            .Select(p => new TrustedPeerRow
            {
                Fingerprint = p.Fingerprint,
                Alias = ViewModel.DeviceNicknames.Get(p.Fingerprint) ?? p.Alias,
                ApprovedAt = p.ApprovedAt,
            })
            .ToList());

    public Task<IReadOnlyList<DeniedPeerRow>> LoadDeniedDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeniedPeerRow>>(ViewModel.TrustedPeers.LoadDenied()
            .OrderByDescending(p => p.DeniedAt)
            .Select(p => new DeniedPeerRow { Fingerprint = p.Fingerprint, Alias = p.Alias, DeniedAt = p.DeniedAt })
            .ToList());

    public async Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default)
    {
        await ViewModel.TrustedPeers.RevokeAsync(device.Fingerprint);
        // Best-effort - lets the peer clear its own stale pairing proactively if
        // it's currently reachable; harmless no-op otherwise, since it falls back
        // to discovering the revoke passively either way (see PeerUnpairNotifier).
        ViewModel.PeerUnpair?.NotifyFireAndForget(device.Fingerprint);
    }

    public Task ForgetDenialAsync(DeniedPeerRow device, CancellationToken ct = default) =>
        ViewModel.TrustedPeers.ForgetDenialAsync(device.Fingerprint);

    public async Task RenameDeviceAsync(TrustedPeerRow device, CancellationToken ct = default)
    {
        await ViewModel.DeviceNicknames.SetAsync(device.Fingerprint, device.Alias);

        // Without this, a rename made here only ever reaches the sidebar (and the
        // device-detail pane, which shares the same SidebarItem) once that device
        // happens to be mDNS-rediscovered again - which might not happen again all
        // session if it stays continuously connected.
        ViewModel.RefreshDeviceDisplayNames();
    }

    public Task RescanAsync(CancellationToken ct = default) => ViewModel.RescanLibraryAsync();

    public Task RebuildDatabaseAsync(CancellationToken ct = default)
    {
        ViewModel.RebuildDatabaseCommand?.Execute(null);
        return Task.CompletedTask;
    }

    public Task<string> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) =>
        throw new NotSupportedException("An app peer pairs by approving a request, not by issuing a code.");

    public Task<IReadOnlyList<SubsonicCredentialRow>> LoadSubsonicCredentialsAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("Only a Flower server issues Subsonic credentials.");

    public Task<SubsonicCredentialRow> IssueSubsonicCredentialAsync(string label, CancellationToken ct = default) =>
        throw new NotSupportedException("Only a Flower server issues Subsonic credentials.");

    public Task RevokeSubsonicCredentialAsync(SubsonicCredentialRow credential, CancellationToken ct = default) =>
        throw new NotSupportedException("Only a Flower server issues Subsonic credentials.");

    public Task<IReadOnlyList<string>> LoadLogAsync(int limit, CancellationToken ct = default) =>
        throw new NotSupportedException("The app has its own Log window.");

    // Describes where play counts will actually come from without doing any slow
    // work itself (the live export - see ITunesPlayCountImporter - isn't triggered
    // just to populate this label; only checking whether Music.app is installed at
    // all, and whether a fallback file exists).
    private static string DescribeITunesLibrarySource()
    {
        if (Directory.Exists("/System/Applications/Music.app") || Directory.Exists("/Applications/Music.app"))
            return "Exports a fresh copy from Music.app each launch";

        return ITunesPlayCountImporter.ResolveLibraryXmlPath() is string fallbackPath
            ? $"Music.app not found - using {fallbackPath}"
            : "No iTunes/Music library data available";
    }
}
