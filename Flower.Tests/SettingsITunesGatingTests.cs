using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Flower.Logging;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// The rule the settings screen enforces for the three iTunes/Music.app switches,
// on either backend: they are only usable when the machine being configured
// actually has a Music.app media folder to integrate with. With no folder found
// the switches are disabled and the screen says why instead - see
// SettingsViewModel.HasAppleMusicFolder and SettingsPanel.axaml's Library tab.
//
// Worth its own tests because the two failure shapes are silent ones: switches
// that look available but can never do anything (a Linux server), and switches
// that go dead with no explanation.
public class SettingsITunesGatingTests
{
    // Only LoadAsync and Capabilities matter here; everything else on the
    // interface belongs to tabs this test never touches, so the unsupported
    // throws are the honest answer rather than a stub returning empty.
    private sealed class StubBackend(SettingsCapabilities capabilities, SettingsSnapshot snapshot) : ISettingsBackend
    {
        public SettingsCapabilities Capabilities { get; } = capabilities;

        public Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(snapshot);

        public Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public int CountSongsUnder(string folder) => -1;

        public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrustedPeerRow>>([]);

        public Task<IReadOnlyList<DeniedPeerRow>> LoadDeniedDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DeniedPeerRow>>([]);

        public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => Task.CompletedTask;
        public Task ForgetDenialAsync(DeniedPeerRow device, CancellationToken ct = default) => Task.CompletedTask;
        public Task RenameDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SubsonicCredentialRow>> LoadSubsonicCredentialsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SubsonicCredentialRow>>([]);

        public Task<SubsonicCredentialRow> IssueSubsonicCredentialAsync(string label, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeSubsonicCredentialAsync(SubsonicCredentialRow credential, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RescanAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RebuildDatabaseAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<InMemoryLogEntry>> LoadLogAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InMemoryLogEntry>>([]);

        public Task<IReadOnlyList<InMemoryLogEntry>?> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<InMemoryLogEntry>?>([]);
    }

    // The server's capability set (see RemoteServerSettingsBackend), which now
    // includes the iTunes switches: the machine a headless server runs on is very
    // often the Mac whose Music.app library it is meant to serve.
    private static readonly SettingsCapabilities ServerCapabilities = new()
    {
        ITunesIntegration = true,
        ServerNetwork = true,
        PairingCodes = true,
        SubsonicCredentials = true,
        Log = true,
    };

    private static async Task<SettingsViewModel> LoadAsync(SettingsCapabilities capabilities, SettingsSnapshot snapshot)
    {
        var viewModel = new SettingsViewModel(new StubBackend(capabilities, snapshot));
        await viewModel.LoadAsync();
        return viewModel;
    }

    [Fact]
    public async Task All_three_switches_are_usable_when_a_music_folder_was_found()
    {
        var viewModel = await LoadAsync(ServerCapabilities, new SettingsSnapshot
        {
            AppleMusicFolder = "/Users/x/Music/Music/Media.localized",
            IntegrateWithITunes = true,
            SyncPlayCountFromITunes = true,
            SyncDateAddedFromITunes = true,
        });

        Assert.True(viewModel.HasAppleMusicFolder);
        Assert.True(viewModel.CanIntegrateWithITunes);
        Assert.True(viewModel.CanSyncFromITunes);
        Assert.False(viewModel.ShowsITunesUnavailable);
        Assert.True(viewModel.IntegrateWithITunes);
        Assert.True(viewModel.SyncPlayCountFromITunes);
        Assert.True(viewModel.SyncDateAddedFromITunes);
    }

    [Fact]
    public async Task No_music_folder_disables_all_three_and_explains_why()
    {
        var viewModel = await LoadAsync(ServerCapabilities, new SettingsSnapshot
        {
            AppleMusicFolder = null,
            IntegrateWithITunes = true,
            SyncPlayCountFromITunes = true,
            SyncDateAddedFromITunes = true,
        });

        Assert.False(viewModel.HasAppleMusicFolder);
        Assert.False(viewModel.CanIntegrateWithITunes);
        Assert.False(viewModel.CanSyncFromITunes);
        Assert.True(viewModel.ShowsITunesUnavailable);
        Assert.Contains("server", viewModel.ITunesUnavailableMessage, StringComparison.OrdinalIgnoreCase);
    }

    // The message names the machine it is talking about, and for the app that is
    // this device rather than a server - the same panel renders both.
    [Fact]
    public async Task The_apps_own_message_names_this_device_rather_than_a_server()
    {
        var viewModel = await LoadAsync(
            new SettingsCapabilities { ITunesIntegration = true, PairedServerPicker = true, ThemePicker = true },
            new SettingsSnapshot { AppleMusicFolder = null });

        Assert.True(viewModel.ShowsITunesUnavailable);
        Assert.Contains("this device", viewModel.ITunesUnavailableMessage, StringComparison.OrdinalIgnoreCase);
    }

    // The two per-track imports still follow the master switch when a folder was
    // found - turning the integration off takes them with it, without clearing
    // their own remembered values.
    [Fact]
    public async Task Turning_the_master_switch_off_disables_the_two_imports_without_unchecking_them()
    {
        var viewModel = await LoadAsync(ServerCapabilities, new SettingsSnapshot
        {
            AppleMusicFolder = "/Users/x/Music/Music/Media.localized",
            IntegrateWithITunes = true,
            SyncPlayCountFromITunes = true,
            SyncDateAddedFromITunes = true,
        });

        viewModel.IntegrateWithITunes = false;

        Assert.False(viewModel.CanSyncFromITunes);
        Assert.True(viewModel.SyncPlayCountFromITunes);
        Assert.True(viewModel.SyncDateAddedFromITunes);
        Assert.False(viewModel.ShowsITunesUnavailable);
    }

    // Turning the master switch on is what puts Music.app's own media folder
    // into the library-folder list, on the server exactly as in the app - see
    // SettingsViewModel.ApplyAppleMusicFolder.
    [Fact]
    public async Task The_master_switch_adds_the_music_folder_as_a_library_path()
    {
        const string folder = "/Users/x/Music/Music/Media.localized";
        var viewModel = await LoadAsync(ServerCapabilities, new SettingsSnapshot
        {
            AppleMusicFolder = folder,
            LibraryPaths = ["/music"],
            IntegrateWithITunes = false,
        });

        viewModel.IntegrateWithITunes = true;

        Assert.Contains(viewModel.LibraryPaths, row => row.Path == folder);
    }

    // ...and turning it back off leaves that folder exactly where it is. This is
    // the whole point of the switch not owning the folder: unchecking it used to
    // remove the folder, and for a library that is Music.app's folder and
    // nothing else that meant declining two metadata imports silently emptied
    // the library (a scan of no folders finds nothing - see Importer.Import).
    // Removing the folder is Remove Folder's job.
    [Fact]
    public async Task Turning_the_master_switch_off_leaves_the_music_folder_in_the_library()
    {
        const string folder = "/Users/x/Music/Music/Media.localized";
        var viewModel = await LoadAsync(ServerCapabilities, new SettingsSnapshot
        {
            AppleMusicFolder = folder,
            LibraryPaths = ["/music", folder],
            IntegrateWithITunes = true,
        });

        viewModel.IntegrateWithITunes = false;

        Assert.Contains(viewModel.LibraryPaths, row => row.Path == folder);
        Assert.Contains(viewModel.LibraryPaths, row => row.Path == "/music");
    }
}
