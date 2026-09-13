using Flower.Audio.Ffmpeg;

using Xunit;

namespace Flower.Tests;

// FfmpegTrackDecoder.IsStalled is the predicate behind the "Decode watchdog"
// warning. It is separated from the watchdog method - which needs a live
// decode thread and a native decoder - because the interesting cases are
// exactly the ones a real decode cannot be asked to produce on demand, and
// because getting it wrong is not a quiet failure: the LibVLC watchdog's
// version without the backpressure term logged 2430 false alarms in a single
// day on one phone, every one of them with the target ring at exactly
// 384000/384000 and nothing at all wrong.
public class DecodeWatchdogTests
{
    // A decoder that filled its ring and is waiting for playback to drain it
    // produces no bytes, which is the healthy steady state for most of an
    // armed track rather than a wedge.
    [Fact]
    public void Waiting_for_room_is_not_a_stall()
    {
        Assert.False(FfmpegTrackDecoder.IsStalled(
            decoding: true,
            bytesProduced: 1000, lastBytesProduced: 1000,
            backpressureWaits: 57, lastBackpressureWaits: 12));
    }

    // The case the watchdog exists for: decoding, producing nothing, and not
    // because it is waiting for room - so it is parked somewhere else, which
    // for this decoder means inside a read that is not coming back.
    [Fact]
    public void Producing_nothing_without_waiting_for_room_is_a_stall()
    {
        Assert.True(FfmpegTrackDecoder.IsStalled(
            decoding: true,
            bytesProduced: 1000, lastBytesProduced: 1000,
            backpressureWaits: 12, lastBackpressureWaits: 12));
    }

    [Fact]
    public void A_decoder_still_producing_bytes_is_never_a_stall()
    {
        Assert.False(FfmpegTrackDecoder.IsStalled(
            decoding: true,
            bytesProduced: 2000, lastBytesProduced: 1000,
            backpressureWaits: 12, lastBackpressureWaits: 12));
    }

    // The very first tick has no previous sample of either counter, so there
    // is no delta to conclude anything from and it must report nothing. The
    // watchdog runs once a second and there is always a next one.
    [Fact]
    public void The_first_tick_concludes_nothing()
    {
        Assert.False(FfmpegTrackDecoder.IsStalled(
            decoding: true,
            bytesProduced: 0, lastBytesProduced: -1,
            backpressureWaits: 0, lastBackpressureWaits: -1));
    }

    // A tick that has a byte-count history but no backpressure history yet
    // cannot tell "parked for room" from "wedged", and the safe reading of an
    // unknown is the loud one: this is the only branch that can ever report
    // the wedge the watchdog exists for, and a decoder that has genuinely
    // stopped on its first two ticks should not go unmentioned for want of a
    // third sample.
    [Fact]
    public void A_missing_backpressure_history_still_reports_a_stall()
    {
        Assert.True(FfmpegTrackDecoder.IsStalled(
            decoding: true,
            bytesProduced: 1000, lastBytesProduced: 1000,
            backpressureWaits: 5, lastBackpressureWaits: -1));
    }

    // Retired and finished decoders both land here, and neither is a stall: a
    // decoder that has stopped on purpose produces no bytes for the rest of
    // its life, and reporting that once a second is how a watchdog becomes
    // noise nobody reads.
    [Fact]
    public void A_decoder_that_is_not_decoding_is_never_a_stall()
    {
        Assert.False(FfmpegTrackDecoder.IsStalled(
            decoding: false,
            bytesProduced: 1000, lastBytesProduced: 1000,
            backpressureWaits: 12, lastBackpressureWaits: 12));
    }
}
