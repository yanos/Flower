using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Logging;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Settings;

// A server's icon from its settings page: a picture chosen is made into the
// small square the server keeps before it is sent, and takes the place of an
// emoji, as the server makes it take its place there.
public class SettingsServerIconTests
{
    private sealed class Backend : ISettingsBackend
    {
        public List<byte[]> Sent { get; } = [];

        public SettingsCapabilities Capabilities { get; } = new() { ServerIconPicker = true };

        public Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(new SettingsSnapshot { IconEmoji = "🌻" });
        public Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public int CountSongsUnder(string folder) => 0;
        public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string Code, string Invite)> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RescanAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RebuildDatabaseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<LogSlice> LoadLogAsync(int limit, long afterSequence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InMemoryLogEntry>?> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemovedFileRow>> LoadRemovedFilesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RemovedFileRow>>([]);
        public Task<string> RestoreRemovedFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> DeleteRemovedFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetServerIconAsync(byte[] png, CancellationToken ct = default)
        {
            Sent.Add(png);
            return Task.CompletedTask;
        }
    }

    [AvaloniaFact]
    public async Task A_picture_chosen_is_sent_as_a_small_square_and_replaces_the_emoji()
    {
        var backend = new Backend();
        var viewModel = new SettingsViewModel(backend);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(viewModel.PreviewsIconEmoji);

        await viewModel.SetIconPictureAsync(SyntheticPng.Build(800, 400));

        var sent = Assert.Single(backend.Sent);
        var size = AlbumArtLoader.MeasureArt(sent);
        Assert.Equal(ServerIcon.ImagePixels, size?.Width);
        Assert.Equal(ServerIcon.ImagePixels, size?.Height);
        Assert.Equal("", viewModel.IconEmoji);
        Assert.True(viewModel.PreviewsIconPicture);
    }

    [AvaloniaFact]
    public async Task A_file_that_is_not_a_picture_is_refused_before_anything_is_sent()
    {
        var backend = new Backend();
        var viewModel = new SettingsViewModel(backend);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);

        await viewModel.SetIconPictureAsync("not a picture"u8.ToArray());

        Assert.Empty(backend.Sent);
        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Equal("🌻", viewModel.IconEmoji);
    }
}
