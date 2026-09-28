using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// The OS's Now Playing card (lock screen, Control Center) gets the art of the
// song playing - and not only a song with a file on this device. A streamed
// song has no file, and its card was blank.
[Collection("PlatformDataDirectory")]
public class NowPlayingIntegrationServiceTests : PinnedDataDirectory
{
    private sealed class FakeCard : IPlatformNowPlaying
    {
        public event EventHandler<NowPlayingCommand>? CommandReceived { add { } remove { } }
        public List<NowPlayingMetadata> Published { get; } = new();
        public void UpdateMetadata(NowPlayingMetadata metadata) => Published.Add(metadata);
        public void UpdatePlaybackState(bool isPlaying, TimeSpan elapsed) { }
        public void Clear() { }
    }

    // Stands in for the server: "fetching" an album's art writes it to the
    // disk cache, the way LoadRemoteAsync does, and answers a bitmap.
    private sealed class FetchingArtLoader(byte[] bytes) : AlbumArtLoader(null, null, NullLogger<AlbumArtLoader>.Instance)
    {
        public int Fetches { get; private set; }

        public override async Task<Bitmap?> LoadAsync(Track track)
        {
            Fetches++;
            await Task.Yield();
            var directory = Path.Combine(AppDataDirectory.Path, "AlbumArtCache");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, $"{track.OriginAlbumArtId}.art"), bytes);
            return new RenderTargetBitmap(new PixelSize(4, 4));
        }
    }

    private static readonly byte[] Art = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];

    private static Track Streamed(string title, string albumId) => new()
    {
        Title = title,
        Album = title + " album",
        Path = "https://server.local:4534/api/flower/v1/stream?id=" + title,
        OriginAlbumArtId = albumId,
        OriginDeviceFingerprint = "fingerprint",
    };

    private readonly IPlatformNowPlaying? _previousCard = PlatformNowPlaying.Current;

    public override void Dispose()
    {
        PlatformNowPlaying.Current = _previousCard;
        base.Dispose();
    }

    private static (PlaylistControlViewModel Playback, FakeCard Card, NowPlayingIntegrationService Service) Build(
        AlbumArtLoader art, params Track[] tracks)
    {
        var card = new FakeCard();
        PlatformNowPlaying.Current = card;
        var playback = new PlaylistControlViewModel(
            new FakeAudioManager(), new MainPlaylist(new List<Track>(tracks)), new Library(new List<Track>(tracks)),
            new AppSettings(), new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance);
        playback.OffPlaybackThread = work => work();
        var service = new NowPlayingIntegrationService(playback, new FakeAudioManager(), art,
            NullLogger<NowPlayingIntegrationService>.Instance);
        return (playback, card, service);
    }

    private static void WaitFor(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 2000;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    [AvaloniaFact]
    public void A_streamed_song_whose_art_is_not_cached_gets_it_once_fetched()
    {
        var art = new FetchingArtLoader(Art);
        var song = Streamed("Maps", "album-1");
        var (playback, card, service) = Build(art, song);
        using var _ = service;

        playback.Play(song);
        WaitFor(() => card.Published.Count >= 2);

        Assert.Equal(1, art.Fetches);
        Assert.Null(card.Published[0].ArtworkData);
        Assert.Equal(Art, card.Published[^1].ArtworkData);
        Assert.Equal("Maps", card.Published[^1].Title);
    }

    [AvaloniaFact]
    public void A_streamed_song_whose_art_is_cached_has_it_straight_away()
    {
        var directory = Path.Combine(AppDataDirectory.Path, "AlbumArtCache");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "album-1.art"), Art);
        var art = new FetchingArtLoader(Art);
        var song = Streamed("Maps", "album-1");
        var (playback, card, service) = Build(art, song);
        using var _ = service;

        playback.Play(song);
        WaitFor(() => card.Published.Count >= 1);

        Assert.Equal(Art, card.Published[0].ArtworkData);
        Assert.Equal(0, art.Fetches);
    }

    // Art that arrives after the song has moved on is not put on the next
    // song's card.
    [AvaloniaFact]
    public void Art_that_arrives_late_is_not_put_on_the_next_song()
    {
        var art = new FetchingArtLoader(Art);
        var first = Streamed("Maps", "album-1");
        var second = Streamed("Game of Pricks", "album-2");
        var (playback, card, service) = Build(art, first, second);
        using var _ = service;

        playback.Play(first);
        playback.Play(second);
        WaitFor(() => art.Fetches == 2 && card.Published.Count >= 3);

        Assert.All(card.Published, m => Assert.True(m.ArtworkData == null || m.Title == "Game of Pricks"));
    }
}
