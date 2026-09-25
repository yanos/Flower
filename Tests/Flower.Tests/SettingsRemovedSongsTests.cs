using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Logging;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// The Library tab's "Removed Songs": what a backend reports is listed, and
// Restore hands the paths back to it and shows the list as it now is.
public class SettingsRemovedSongsTests
{
    private sealed class Backend : ISettingsBackend
    {
        public List<RemovedFileRow> Removed { get; } = [];
        public List<string> Restored { get; } = [];

        public SettingsCapabilities Capabilities { get; } = new();

        public Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(new SettingsSnapshot());
        public Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public int CountSongsUnder(string folder) => 0;
        public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DeniedPeerRow>> LoadDeniedDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ForgetDenialAsync(DeniedPeerRow device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string Code, string Invite)> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RescanAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RebuildDatabaseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<LogSlice> LoadLogAsync(int limit, long afterSequence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InMemoryLogEntry>?> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemovedFileRow>> LoadRemovedFilesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemovedFileRow>>(Removed.ToList());

        public Task<string> RestoreRemovedFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            Restored.AddRange(paths);
            Removed.RemoveAll(r => paths.Contains(r.Path));
            return Task.FromResult($"Restored {paths.Count}");
        }
    }

    private static RemovedFileRow Row(string path) => new(path, DateTimeOffset.UtcNow, StillOnDisk: true);

    [Fact]
    public async Task Nothing_removed_means_no_section()
    {
        var viewModel = new SettingsViewModel(new Backend());

        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        Assert.False(viewModel.HasRemovedFiles);
    }

    [Fact]
    public async Task Restoring_one_file_restores_that_file_and_refreshes_the_list()
    {
        var backend = new Backend();
        backend.Removed.AddRange([Row("/music/A/One.mp3"), Row("/music/A/Two.mp3")]);
        var viewModel = new SettingsViewModel(backend);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.HasRemovedFiles);

        await viewModel.RestoreRemovedFileCommand.ExecuteAsync(viewModel.RemovedFiles[0]);

        Assert.Equal(["/music/A/One.mp3"], backend.Restored);
        Assert.Equal(["Two.mp3"], viewModel.RemovedFiles.Select(r => r.FileName));
        Assert.Equal("Restored 1", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Restore_all_empties_the_list_and_hides_the_section()
    {
        var backend = new Backend();
        backend.Removed.AddRange([Row("/music/A/One.mp3"), Row(@"C:\Music\B\Two.flac")]);
        var viewModel = new SettingsViewModel(backend);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        await viewModel.RestoreAllRemovedFilesCommand.ExecuteAsync(null);

        Assert.Equal(2, backend.Restored.Count);
        Assert.False(viewModel.HasRemovedFiles);
    }

    // The path is the server's when the panel is administering one, so the
    // row splits it on either separator rather than this machine's.
    [Theory]
    [InlineData("/srv/music/Band/Song.flac", "Song.flac", "/srv/music/Band")]
    [InlineData(@"D:\Music\Band\Song.flac", "Song.flac", @"D:\Music\Band")]
    public void A_row_names_the_file_and_its_folder_whichever_machine_the_path_is_from(string path, string file, string folder)
    {
        var row = Row(path);

        Assert.Equal(file, row.FileName);
        Assert.Equal(folder, row.Folder);
    }
}
