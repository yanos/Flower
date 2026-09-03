using Flower.Audio;
using Flower.Logging;

using LibVLCSharp.Shared;

using Xunit;

namespace Flower.Tests;

// TrackDecoder.IsStalled is the predicate behind the "Decode watchdog"
// warning. It is separated from the watchdog method - which can only be
// driven through a real MediaPlayer - because the interesting cases are
// exactly the ones a real decode cannot be asked to produce on demand, and
// because getting it wrong is not a quiet failure: the version without the
// backpressure term logged 2430 false alarms in a single day on one phone,
// every one of them with the target ring at exactly 384000/384000.
//
// No [Trait("Category", "RequiresLibVLC")] here despite the VLCState
// reference: naming the enum does not load the native library.
public class DecodeWatchdogTests
{
    // A decoder that filled its ring and is waiting for playback to drain it
    // produces no bytes, which is the healthy steady state for most of a
    // track rather than a wedge.
    [Fact]
    public void Waiting_for_room_is_not_a_stall()
    {
        Assert.False(TrackDecoder.IsStalled(
            VLCState.Playing, isPlaying: true,
            bytesProduced: 1000, lastBytesProduced: 1000,
            backpressureWaits: 57, lastBackpressureWaits: 12));
    }

    // The case the watchdog exists for: playing, claiming to play, producing
    // nothing, and not because it is waiting for room.
    [Fact]
    public void Producing_nothing_without_waiting_for_room_is_a_stall()
    {
        Assert.True(TrackDecoder.IsStalled(
            VLCState.Playing, isPlaying: true,
            bytesProduced: 1000, lastBytesProduced: 1000,
            backpressureWaits: 12, lastBackpressureWaits: 12));
    }

    [Fact]
    public void A_decoder_still_producing_bytes_is_never_a_stall()
    {
        Assert.False(TrackDecoder.IsStalled(
            VLCState.Playing, isPlaying: true,
            bytesProduced: 2000, lastBytesProduced: 1000,
            backpressureWaits: 12, lastBackpressureWaits: 12));
    }

    // The very first tick has no previous sample of either counter, so there
    // is no delta to conclude anything from and it must report nothing. The
    // watchdog runs once a second and there is always a next one.
    [Fact]
    public void The_first_tick_concludes_nothing()
    {
        Assert.False(TrackDecoder.IsStalled(
            VLCState.Playing, isPlaying: true,
            bytesProduced: 0, lastBytesProduced: -1,
            backpressureWaits: 0, lastBackpressureWaits: -1));
    }

    [Theory]
    [InlineData(VLCState.Paused, true)]
    [InlineData(VLCState.Stopped, true)]
    [InlineData(VLCState.Ended, true)]
    [InlineData(VLCState.Playing, false)]
    public void Anything_not_actually_playing_is_someone_elses_finding(VLCState state, bool isPlaying)
    {
        // StateMismatch and UnexpectedState are separate flags on the same
        // warning; the stall term must not also claim these.
        Assert.False(TrackDecoder.IsStalled(
            state, isPlaying,
            bytesProduced: 1000, lastBytesProduced: 1000,
            backpressureWaits: 12, lastBackpressureWaits: 12));
    }

    [Fact]
    public void A_local_path_is_logged_as_it_is()
    {
        Assert.Equal("/Users/x/Music/a.flac", LogPath.Short("/Users/x/Music/a.flac"));
    }

    // The stream URL carries the whole authenticated request: ~900 characters
    // that swamp the line, including the `t=` token, which is a credential
    // that a client then pushes into the server's device log to sit at rest.
    [Fact]
    public void A_stream_url_keeps_the_server_and_the_track_and_drops_the_credentials()
    {
        var url = "http://169.254.116.39:4533/rest/stream?u=&t=ea6bc7ff3d9f43be1969453e948b1096"
            + "&s=0fd4bcfadc28aadc&v=1.16.1&c=Flower&f=json"
            + "&id=7b59944d35084c53898564046b766559&X-Flower-Fingerprint=5306d3acebf0e49870b4e44f338afd1c";

        var logged = LogPath.Short(url);

        Assert.Equal("http://169.254.116.39:4533/rest/stream?id=7b59944d35084c53898564046b766559", logged);
        Assert.DoesNotContain("ea6bc7ff3d9f43be1969453e948b1096", logged);
    }

    [Fact]
    public void A_url_without_a_track_id_still_loses_its_query()
    {
        Assert.Equal(
            "http://host:4533/rest/ping",
            LogPath.Short("http://host:4533/rest/ping?u=&t=secret&c=Flower"));
    }
}
