using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

namespace Flower.Tests;

// What a greyed-out track (an undownloaded placeholder with nobody to stream
// it from - see TrackAvailability) must still do, and must not do: activating
// it is a quiet no-op rather than a crash, it stays selectable so it can be
// dragged into a playlist, and it does not claim to be the track playing.
public class UnavailableTrackInteractionTests
{
    private static Track Placeholder(string title) => new()
    {
        Title = title,
        Album = "Remote",
        Path = null,
        OriginTrackId = $"origin-{title}",
        OriginDeviceFingerprint = "server",
    };

    private static Track Local(string title) => new()
    {
        Title = title,
        Album = "Remote",
        Path = $"/music/{title}.mp3",
    };

    private sealed class DecliningResolver : IStreamUrlResolver
    {
        public Task<string?> ResolveAsync(Track track) => Task.FromResult<string?>(null);
    }

    // A resolver that violates its own contract. It should still not reach the
    // user as an exception: Play reads the resolve task's result on the UI
    // thread, so an unguarded throw there crashed on activating the row.
    private sealed class ThrowingResolver : IStreamUrlResolver
    {
        public Task<string?> ResolveAsync(Track track) =>
            throw new InvalidOperationException("the peer went away mid-resolve");
    }

    private sealed class FaultingResolver : IStreamUrlResolver
    {
        public async Task<string?> ResolveAsync(Track track)
        {
            await Task.Yield();
            throw new InvalidOperationException("the peer went away mid-resolve");
        }
    }

    private static PlaylistControlViewModel Playback(
        List<Track> tracks, out FakeAudioManager audio, IStreamUrlResolver? resolver)
    {
        audio = new FakeAudioManager();
        return new PlaylistControlViewModel(
            audio, new MainPlaylist(tracks), new Library(tracks), new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance, resolver);
    }

    [Fact]
    public void Playing_an_unavailable_track_starts_nothing_and_throws_nothing()
    {
        var track = Placeholder("Gone");
        var vm = Playback([track], out var audio, new DecliningResolver());

        vm.Play(track, 0);

        Assert.Null(audio.LastPlayed);
        Assert.Null(vm.CurrentlyPlayingTrack);
    }

    [Fact]
    public void A_resolver_that_throws_synchronously_does_not_reach_the_caller()
    {
        var track = Placeholder("Gone");
        var vm = Playback([track], out var audio, new ThrowingResolver());

        var ex = Record.Exception(() => vm.Play(track, 0));

        Assert.Null(ex);
        Assert.Null(audio.LastPlayed);
    }

    [Fact]
    public async Task A_resolver_that_faults_asynchronously_does_not_reach_the_caller()
    {
        var track = Placeholder("Gone");
        var vm = Playback([track], out var audio, new FaultingResolver());

        var ex = Record.Exception(() => vm.Play(track, 0));
        await Task.Delay(50);

        Assert.Null(ex);
        Assert.Null(audio.LastPlayed);
    }

    [Fact]
    public void With_no_stream_resolver_at_all_an_unavailable_track_is_simply_not_played()
    {
        var track = Placeholder("Gone");
        var vm = Playback([track], out var audio, resolver: null);

        var ex = Record.Exception(() => vm.Play(track, 0));

        Assert.Null(ex);
        Assert.Null(audio.LastPlayed);
    }

    // Nothing playing must not read as "everything is playing". Every
    // placeholder has a null Path, so a path comparison put the little play
    // triangle on all of them at once.
    [Fact]
    public void No_row_claims_to_be_playing_while_nothing_is()
    {
        var rows = TrackListBuilder.Build(
            [Placeholder("One"), Placeholder("Two"), Local("Three")],
            null, "Title", true, currentlyPlayingTrack: null);

        Assert.All(rows, row => Assert.False(row.IsCurrentlyPlaying));
    }

    [Fact]
    public void Only_the_playing_placeholder_shows_the_indicator()
    {
        var playing = Placeholder("One");
        var rows = TrackListBuilder.Build(
            [playing, Placeholder("Two")], null, "Title", true, currentlyPlayingTrack: playing);

        Assert.True(rows[0].IsCurrentlyPlaying);
        Assert.False(rows[1].IsCurrentlyPlaying);
    }

    // A streamed placeholder plays as a transient copy carrying the stream URL
    // (Track.Clone keeps the Id), so its Path matches nothing in the library -
    // by path it was the one playing track that never showed its own indicator.
    [Fact]
    public void The_streaming_copy_of_a_placeholder_still_lights_its_own_row()
    {
        var placeholder = Placeholder("One");
        var streaming = placeholder.Clone();
        streaming.Path = "http://server/stream?id=origin-One";

        var rows = TrackListBuilder.Build(
            [placeholder, Placeholder("Two")], null, "Title", true, currentlyPlayingTrack: streaming);

        Assert.True(rows[0].IsCurrentlyPlaying);
        Assert.False(rows[1].IsCurrentlyPlaying);
    }
}
