using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Manager;
using Flower.Models;
using Flower.Tests.TestSupport;

using LibVLCSharp.Shared;

namespace Flower.Tests;

// Real TrackDecoder/GaplessCoordinator decoding audio served over an actual
// HTTP connection (FakePeerHttpServer), rather than a local file - the same
// "://" FromLocation path TrackDecoder.EnsureMedia takes for a synced peer's
// stream URL (OpenSubsonicClient.GetStreamUrl) or a content:// Android URI.
// TrackDecoderTests/GaplessCoordinatorRealDecodeTests already cover local-
// file decode in depth; what's new here is real socket-level failure modes a
// local file can never produce - a peer that's simply not there, and one
// that drops the connection partway through - proving the gapless pipeline
// settles (Faulted/Drained/EndReached) instead of hanging, and that
// GaplessCoordinator still promotes whatever's armed next exactly as it does
// for a track that ends normally. Requires a real VLC install, same as every
// other RequiresLibVLC test.
[Trait("Category", "RequiresLibVLC")]
[Collection("LibVLC")]
public class StreamingNetworkOutageTests : IDisposable
{
    private readonly LibVLC _libVLC;
    private readonly string _tempDir;

    public StreamingNetworkOutageTests(LibVlcFixture fixture)
    {
        _libVLC = fixture.LibVLC;
        _tempDir = Directory.CreateTempSubdirectory("flower-streaming-outage-tests-").FullName;
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static Track MakeTrack(string path, TimeSpan duration) =>
        new() { Title = "Test", Path = path, Duration = duration };

    private static int BytesFor(TimeSpan duration) =>
        (int)(duration.TotalSeconds * GaplessFormat.SampleRate) * GaplessFormat.BytesPerFrame;

    private static void WaitUntil(Func<bool> condition, string because, int timeoutSeconds = 15) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(timeoutSeconds)), because);

    // Serves the whole file in one shot with a correct Content-Length - the
    // "peer is up and the network is fine" baseline every outage test below
    // is a variation on.
    private static FakePeerHttpServer ServeWholeFile(byte[] bytes) => new(async ctx =>
    {
        ctx.Response.ContentType = "audio/x-wav";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.OutputStream.Close();
    });

    // Accepts the connection and starts responding, then resets it before
    // the declared Content-Length is fully sent - Response.Abort() tears
    // down the connection outright (no clean FIN, no final chunk), which is
    // what a real dropped Wi-Fi connection or a killed peer process looks
    // like at the socket level.
    private static FakePeerHttpServer ServeThenDropConnection(byte[] bytes, double fractionBeforeDrop) => new(async ctx =>
    {
        ctx.Response.ContentLength64 = bytes.Length;
        var sendBytes = (int)(bytes.Length * fractionBeforeDrop);
        await ctx.Response.OutputStream.WriteAsync(bytes.AsMemory(0, sendBytes));
        await ctx.Response.OutputStream.FlushAsync();
        ctx.Response.Abort();
    });

    [Fact]
    public void Streaming_a_track_over_http_decodes_successfully_and_fires_Drained()
    {
        var duration = TimeSpan.FromSeconds(1);
        var bytes = SyntheticWav.Build(duration, SyntheticWav.Marker(0x11));
        using var server = ServeWholeFile(bytes);
        var ring = new GaplessRingBuffer(BytesFor(duration) + 4096);
        var decoder = new TrackDecoder(_libVLC, MakeTrack($"http://127.0.0.1:{server.Port}/track.wav", duration), ring);

        var drained = false;
        decoder.Drained += () => drained = true;

        decoder.StartDecoding();

        WaitUntil(() => drained, "Drained should fire once a network-streamed track finishes decoding");
        Assert.InRange((double)decoder.BytesProduced, BytesFor(duration) * 0.9, BytesFor(duration) * 1.1);

        decoder.Retire();
    }

    [Fact]
    public void Faulted_fires_when_the_peer_is_completely_unreachable()
    {
        var unboundPort = FakePeerHttpServer.GetUnboundPort();
        var ring = new GaplessRingBuffer(65536);
        var decoder = new TrackDecoder(_libVLC, MakeTrack($"http://127.0.0.1:{unboundPort}/track.wav", TimeSpan.FromSeconds(1)), ring);

        var faulted = false;
        decoder.Faulted += () => faulted = true;

        decoder.StartDecoding();

        WaitUntil(() => faulted, "Faulted should fire when the peer refuses the connection outright");

        decoder.Retire();
    }

    // The key "handled gracefully" property for a mid-stream outage: it
    // doesn't matter to this test whether LibVLC reports the abrupt cutoff
    // as an error (Faulted) or just treats it like the track quietly ending
    // (Drained) - both are already proven equivalent to
    // GaplessCoordinator/PlaylistControlViewModel (see
    // GaplessCoordinatorTests' Faulted_current_track_behaves_the_same_as_Drained
    // and GaplessAudioManagerTests' Faulted-forwarding case). What actually
    // matters, and is new here, is that a real dropped connection settles
    // one way or the other within a bounded time instead of hanging forever,
    // and that it doesn't fabricate the missing tail of the track.
    [Fact]
    public void A_network_outage_mid_stream_settles_instead_of_hanging_forever()
    {
        var duration = TimeSpan.FromSeconds(3);
        var bytes = SyntheticWav.Build(duration, SyntheticWav.Marker(0x22));
        using var server = ServeThenDropConnection(bytes, fractionBeforeDrop: 0.3);
        var ring = new GaplessRingBuffer(BytesFor(duration) + 4096);
        var decoder = new TrackDecoder(_libVLC, MakeTrack($"http://127.0.0.1:{server.Port}/track.wav", duration), ring);

        var settled = false;
        decoder.Drained += () => settled = true;
        decoder.Faulted += () => settled = true;

        decoder.StartDecoding();

        WaitUntil(() => settled, "decode should settle (Drained or Faulted) instead of hanging after a mid-stream connection drop");
        Assert.True(decoder.BytesProduced < BytesFor(duration),
            "a track that was cut off mid-stream should not end up with the full clip's worth of bytes");

        decoder.Retire();
    }

    // Ties the low-level TrackDecoder behavior above to GaplessCoordinator's
    // handover logic: a network outage on the *currently playing* streamed
    // track must still promote whatever's armed next, the same as a track
    // that ends normally - this is what keeps PlaylistControlViewModel's
    // auto-advance working when the outage happens on a synced peer's track
    // mid-playlist rather than getting the whole player stuck.
    [Fact]
    public void Coordinator_promotes_the_armed_next_track_after_the_current_streamed_track_drops_mid_playback()
    {
        var durationA = TimeSpan.FromSeconds(3);
        var bytesA = SyntheticWav.Build(durationA, SyntheticWav.Marker(0x33));
        using var server = ServeThenDropConnection(bytesA, fractionBeforeDrop: 0.3);
        var trackA = MakeTrack($"http://127.0.0.1:{server.Port}/track.wav", durationA);

        var durationB = TimeSpan.FromSeconds(1);
        var trackB = MakeTrack(SyntheticWav.CreateFile(_tempDir, "b.wav", durationB, SyntheticWav.Marker(0x44)), durationB);

        var sharedRing = new GaplessRingBuffer(8 * (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame);
        var coordinator = new GaplessCoordinator(_libVLC, sharedRing, NullLogger<GaplessCoordinator>.Instance, NullLogger<TrackDecoder>.Instance);

        Track? endReachedTrack = null;
        coordinator.EndReached += t => endReachedTrack = t;

        coordinator.Play(trackA);
        coordinator.SetUpcoming(trackB);

        WaitUntil(() => endReachedTrack == trackA, "the dropped-connection track should still reach EndReached");
        WaitUntil(() => coordinator.CurrentTrack == trackB, "the armed next track should be promoted after the outage, same as a normal handover");

        coordinator.Dispose();
    }
}
