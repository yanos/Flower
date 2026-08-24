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
        PairedServerPicker = true,
        TrustedDevices = false,
        RevealAppDataLocation = true,
        RebuildDatabase = true,
        // The app has its own Log window (View > Log...), which does far more
        // than a flat tail - a Logs tab here would be a worse second copy.
        Log = false,
        // Handing out pairing codes and keeping Subsonic credentials are both
        // the server's job; this device redeems a code, it never issues one.
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
        ShareLogsWithPairedServer = ViewModel.ShareLogsWithPairedServer,
        ITunesLibraryDescription = ITunesIntegration.DescribeSource(),
        AppleMusicFolder = Flower.Importer.Importer.TryResolveAppleMusicFolder(),
        DataDirectory = AppDataDirectory.Path,
        Version = typeof(LocalSettingsBackend).Assembly.GetName().Version?.ToString(),
        IsPairedToServer = !string.IsNullOrEmpty(ViewModel.PairedServerFingerprint),
    });

    // The Library tab's *content* only stops making sense once this device is
    // actually pulling its library from a paired server. A device that hasn't
    // paired with anyone yet still has - and can keep curating - its own local
    // library, right up until it picks one.
    //
    // Read off the *live* pairing rather than the snapshot taken when the
    // screen opened, so unpairing re-enables the tab immediately - the panel
    // re-asks on every change.
    public bool CanManageLocalLibrary => string.IsNullOrEmpty(ViewModel.PairedServerFingerprint);

    // Deliberately applied property-by-property rather than through one settings
    // object: each of these setters already persists and already no-ops when the
    // value is unchanged (see MainViewModel), and several have side effects -
    // ThemePreference repaints, the library paths kick off a rescan - that a
    // bulk write would have to reimplement.
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
        ViewModel.ShareLogsWithPairedServer = draft.ShareLogsWithPairedServer;

        if (draft.LibraryPathsChanged)
            await ViewModel.SaveLibraryPathsAsync(draft.LibraryPaths.ToList());

        // Gated on CanManageLocalLibrary as well as each box's own value: a
        // disabled CheckBox still reports whatever it was set to before it went
        // disabled, so trusting IsChecked alone would let a paired device kick
        // off an iTunes import it is not supposed to run.
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

    // Capabilities.TrustedDevices is false here, so the panel never renders the
    // roster that would call any of these - see ISettingsBackend's note on why
    // an unsupported action throws rather than quietly doing nothing. A device
    // that accepts no incoming connections has no roster to show: the list of
    // devices allowed to sync lives on the server, and is edited there.
    private static NotSupportedException NoRoster() =>
        new("This device has no trusted-device roster - it does not accept incoming connections.");

    public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) => throw NoRoster();

    public Task<IReadOnlyList<DeniedPeerRow>> LoadDeniedDevicesAsync(CancellationToken ct = default) => throw NoRoster();

    public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => throw NoRoster();

    public Task ForgetDenialAsync(DeniedPeerRow device, CancellationToken ct = default) => throw NoRoster();

    public Task RenameDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => throw NoRoster();

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

    public Task<IReadOnlyList<string>> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default) =>
        throw new NotSupportedException("The app has its own Log window.");
}
