using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Flower.Audio;
using Flower.Audio.Ffmpeg;
using Flower.Models;
using Flower.Services;


namespace Flower.DeviceChecks;

// Does this platform actually turn a track into the right audio?
//
// Every other test in this repo answers that on a developer's desktop, where
// the answer has never been the interesting one. The bugs have all been
// somewhere else: LibVLC's mp4 demuxer refusing a stream it cannot seek, and
// then .NET on iOS having no synchronous HTTP at all - both invisible to a
// green desktop suite, both found by a person listening to a phone and
// reporting that an album played nothing.
//
// So these are written to run anywhere: no test framework, no HttpListener, no
// filesystem assumptions beyond a temp directory, and no assertion that is not
// about the samples themselves. Flower.Tests runs them as ordinary facts on
// desktop and CI; Flower.DeviceChecks.iOS runs the identical code on a
// simulator or a phone. When the two disagree, the difference is the platform,
// which is the entire point.
//
// What they check is content, not throughput. A decoder that produces the
// right *number* of bytes and none of the right ones passes every byte-count
// assertion ever written, and "the album made no sound" is exactly that
// failure.
public static class DecodeChecks
{
    // Long enough that a seek has somewhere to land, short enough that the
    // whole run is seconds rather than minutes on a simulator.
    private static readonly TimeSpan ShortTrack = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SeekableTrack = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(60);

    // How long the drain is given to catch up with the decoder once decoding
    // has stopped. Emptying a ring is memcpy, so this is not a budget for the
    // work - it is a bound on how long a descheduled thread is waited for
    // before its shortfall is reported as one.
    private static readonly TimeSpan SettlePatience = TimeSpan.FromSeconds(5);

    // A resampler can trim or pad the very edges of a track by a frame or
    // two; a difference of more than this is a real one.
    private const int TailToleranceMs = 50;

    // MP3 and AAC both carry encoder delay and padding, and how much of it a
    // decoder hands back is a decision it is entitled to make. A quarter of a
    // second is well beyond any of that and well short of a missing verse.
    private const int LossyTailToleranceMs = 250;

    // One decoder, named, plus the canonical PCM format decoding through it
    // pins the pipeline to.
    //
    // There is one entry in that list today, and the shape stays anyway. It is
    // what makes "the façade did not load" a failing check instead of a
    // shorter run - which is the failure this file exists because of, and the
    // only reason it is per-decoder rather than a flat sequence of checks.
    public sealed record DecoderUnderTest(
        string Name,
        PcmSampleFormat Format,
        Func<Track, GaplessRingBuffer, ITrackDecoder> Create);

    // Every decoder this platform can actually run. An unloadable façade
    // yields an empty list rather than an exception: what should be reported
    // is a suite that could not check anything, which
    // RequiredDecodersArePresent turns into a failure wherever a caller said
    // it expected one.
    public static IReadOnlyList<DecoderUnderTest> AvailableDecoders()
    {
        var decoders = new List<DecoderUnderTest>();

        if (FfmpegDecoder.IsAvailable)
            decoders.Add(new("FFmpeg", PcmSampleFormat.S24,
                (track, ring) => new FfmpegTrackDecoder(track, ring, sampleFormat: PcmSampleFormat.S24)));

        return decoders;
    }

    public static IReadOnlyList<CheckResult> RunAll()
    {
        var results = new List<CheckResult>();

        // Once per decoder, rather than once. This whole loop was written when
        // there were two, because the suite had been written against
        // TrackDecoder by name and electing FfmpegTrackDecoder moved the
        // entire streaming path - open, probe, range requests, seek, fault -
        // out from under every check here while all of them stayed green. A
        // decoder nobody checks is a decoder nobody has checked, and the first
        // thing that happened when one was elected is that an album played
        // nothing on a phone.
        //
        // LibVLC is gone and the loop is not, because the half of that lesson
        // that still applies is about a decoder silently not being under test.
        //
        // Each decoder is told its own sample format rather than the run
        // setting the canonical one and putting it back. GaplessFormat is
        // process-wide, so moving it would reach every other test sharing
        // this process - and it did: two checks failed intermittently, in the
        // full suite only, for reasons that had nothing to do with what they
        // check.
        var subjects = AvailableDecoders();
        results.AddRange(RequiredDecodersArePresent(subjects));

        foreach (var subject in subjects)
            results.AddRange(RunChecks(subject));

        return results;
    }

    // FLOWER_REQUIRE_DECODERS names the decoders a caller expects this
    // platform to have, comma-separated, and turns a missing one into a
    // failing check rather than a shorter run.
    //
    // Unset everywhere a decoder's absence is a fact about the platform - a
    // phone with no built façade should report what it can decode, not fail
    // for what nobody built. Set on CI, where a decoder going missing is
    // never a fact about the platform: it means the artifact did not build,
    // or built and would not load, and the visible consequence is a green
    // run that quietly checked half of what it used to. That is the exact
    // shape of the bug this whole per-decoder loop exists because of, so the
    // suite is not allowed to shrink in silence.
    private static IEnumerable<CheckResult> RequiredDecodersArePresent(IReadOnlyList<DecoderUnderTest> available)
    {
        var required = Environment.GetEnvironmentVariable("FLOWER_REQUIRE_DECODERS");
        if (required is not { Length: > 0 })
            yield break;

        foreach (var name in required.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var wanted = name;
            yield return Run($"{wanted} is available to be checked at all", () =>
            {
                foreach (var subject in available)
                {
                    if (string.Equals(subject.Name, wanted, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                var present = available.Count == 0 ? "none" : string.Join(", ", Names(available));
                throw new CheckFailedException(
                    $"FLOWER_REQUIRE_DECODERS asked for \"{wanted}\" and this platform has {present}");
            });
        }
    }

    private static IEnumerable<string> Names(IReadOnlyList<DecoderUnderTest> subjects)
    {
        foreach (var subject in subjects)
            yield return subject.Name;
    }

    private static IReadOnlyList<CheckResult> RunChecks(DecoderUnderTest subject)
    {
        var results = new List<CheckResult>();

        // Every format a library actually holds, local and streamed, before
        // the WAV-only checks below go into the streaming path in detail.
        // A whole album of AAC playing silence while mp3 from the same server
        // played fine is the bug that put this loop here: the format was the
        // variable, and nothing was varying it.
        foreach (var fixture in Fixture.All)
        {
            results.Add(Run($"{subject.Name}: {fixture.Name} decodes from a local file", () => FixtureFromDisk(subject, fixture)));
            results.Add(Run($"{subject.Name}: {fixture.Name} decodes when streamed", () => FixtureFromServer(subject, fixture, servesRanges: true, answersHead: true)));
            results.Add(Run($"{subject.Name}: {fixture.Name} decodes when streamed from a server that refuses ranges", () => FixtureFromServer(subject, fixture, servesRanges: false, answersHead: true)));
            results.Add(Run($"{subject.Name}: {fixture.Name} decodes when streamed from a server that refuses HEAD", () => FixtureFromServer(subject, fixture, servesRanges: true, answersHead: false)));
            results.Add(Run($"{subject.Name}: {fixture.Name} decodes when the catalog has the wrong extension for it", () => MislabelledFixtureStillDecodes(subject, fixture)));
            results.Add(Run($"{subject.Name}: {fixture.Name} plays the way pressing play plays it", () => FixtureStartedWithoutPrepare(subject, fixture)));
        }

        results.AddRange(
        [
            Run($"{subject.Name}: A local file decodes to exactly the PCM it holds", () => LocalFileIsExact(subject)),
            Run($"{subject.Name}: A streamed track decodes to exactly the PCM the server holds", () => StreamedIsExact(subject)),
            Run($"{subject.Name}: A server that refuses ranges still decodes", () => WithoutRangesStillDecodes(subject)),
            Run($"{subject.Name}: A seek mid-stream lands in unbroken audio", () => SeekLandsInUnbrokenAudio(subject)),
            Run($"{subject.Name}: A server that is not there reports a failed prepare", () => AbsentServerFailsPrepare(subject)),
            Run($"{subject.Name}: A stream cut mid-track faults rather than ending quietly", () => CutStreamFaults(subject)),
            Run($"{subject.Name}: A throttled server costs a wait, not the track", () => ThrottledStreamStillDecodes(subject, sendsRetryAfter: true)),
            Run($"{subject.Name}: A throttled server that sends no Retry-After still costs only a wait", () => ThrottledStreamStillDecodes(subject, sendsRetryAfter: false)),
            Run($"{subject.Name}: A server that requires a fresh nonce per request still decodes", () => ReplayGuardedStreamStillDecodes(subject)),
            Run($"{subject.Name}: An error on an HTTP 200 is refused rather than decoded", () => ProtocolErrorIsNotAudio(subject)),
        ]);

        return results;
    }

    // One format, off a disk. The baseline each streamed result is read
    // against: a format that fails here fails everywhere, and HTTP is not
    // implicated.
    private static void FixtureFromDisk(DecoderUnderTest subject, Fixture fixture)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flower-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "check." + fixture.Extension);
            File.WriteAllBytes(path, fixture.Bytes());

            AssertPlayable(fixture, DecodeFully(subject, TrackFor(fixture, path)));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A temp directory that outlives the run is not a failed check.
            }
        }
    }

    // The same format over a socket. Ranges off is not an exotic case to be
    // thorough about - it is the exact condition that made VLC's mp4 demuxer
    // discard itself and an album of AAC play nothing, so it earns its own
    // result line per format rather than being folded into the one above.
    private static void FixtureFromServer(DecoderUnderTest subject, Fixture fixture, bool servesRanges, bool answersHead)
    {
        using var server = new LoopbackMediaServer { ServesRanges = servesRanges, AnswersHead = answersHead };
        var url = server.Serve(fixture.Bytes(), "rest/stream?id=" + fixture.Extension);

        AssertPlayable(fixture, DecodeFully(subject, TrackFor(fixture, url)));
    }

    // The catalogued extension is not a fact about the bytes.
    //
    // OriginFileExtension comes from the server's catalog (Child.Suffix), and
    // it describes a file on the server's disk: what arrives on the wire is
    // whatever that server chose to send, which for anything that transcodes
    // is a different container entirely - and a catalog can simply be wrong
    // besides. Both decoders take that extension as a demuxer hint, so this
    // is the check that says a hint is a hint.
    //
    // It is here rather than in a unit test because of how it failed: an mp3
    // whose bytes were not mp3 was forced into the mp3 demuxer, which does
    // not fall back to probing, and the track did not play at all. Nothing on
    // a desktop suite streams, so nothing on a desktop suite had ever handed
    // either decoder a suffix it could not trust.
    // The same streamed decode as FixtureFromServer, started the way Play()
    // starts one: no prepare, no decode-ahead, straight to StartDecoding.
    private static void FixtureStartedWithoutPrepare(DecoderUnderTest subject, Fixture fixture)
    {
        using var server = new LoopbackMediaServer();
        var url = server.Serve(fixture.Bytes(), $"rest/stream?id={fixture.Extension}");

        AssertPlayable(fixture, DecodeFully(subject, TrackFor(fixture, url), prepare: false));
    }

    private static void MislabelledFixtureStillDecodes(DecoderUnderTest subject, Fixture fixture)
    {
        using var server = new LoopbackMediaServer();
        var url = server.Serve(fixture.Bytes(), "rest/stream?id=mislabelled");

        var track = TrackFor(fixture, url);
        track.OriginFileExtension = fixture.Extension == "mp3" ? "flac" : "mp3";

        AssertPlayable(fixture, DecodeFully(subject, track));
    }

    private static Track TrackFor(Fixture fixture, string path) => new()
    {
        Title = fixture.Name,
        Path = path,
        OriginFileExtension = fixture.Extension,
        Duration = Fixture.Duration,
    };

    // Held to whatever its format can prove. Lossless means the fixture's own
    // samples back byte for byte; lossy means audible, in tune, and the right
    // length - see PcmOracle.ToneMismatch for why that is a real bar and not
    // a lowered one.
    private static void AssertPlayable(Fixture fixture, byte[] decoded)
    {
        if (decoded.Length == 0)
            throw new CheckFailedException("no audio came out at all");

        if (PcmOracle.IsSilent(decoded))
            throw new CheckFailedException($"{decoded.Length} bytes of silence");

        if (fixture.ByteExact)
        {
            AssertMatchesSource(Fixture.Wav.Bytes(), decoded);
            return;
        }

        if (PcmOracle.ToneMismatch(decoded, Fixture.ToneHz) is { } complaint)
            throw new CheckFailedException(complaint);

        // A lossy decode still has to be the right *amount* of audio. Both
        // codecs pad, so this is loose - but a track that stops a second early
        // or runs twice as long is not padding.
        var expected = (int)(Fixture.Duration.TotalSeconds * BytesPerSecond());
        var drift = Math.Abs(decoded.Length - expected);
        if (drift > BytesPerSecond() * LossyTailToleranceMs / 1000)
            throw new CheckFailedException($"{drift / (double)BytesPerSecond() * 1000:F0}ms off: expected about {expected} bytes, got {decoded.Length}");
    }

    private static CheckResult Run(string name, Action check)
    {
        var started = Stopwatch.StartNew();
        try
        {
            check();
            return new CheckResult(name, Passed: true, "", started.Elapsed);
        }
        catch (CheckFailedException failed)
        {
            return new CheckResult(name, Passed: false, failed.Message, started.Elapsed);
        }
        catch (Exception unexpected)
        {
            return new CheckResult(name, Passed: false, unexpected.ToString(), started.Elapsed);
        }
    }

    // The baseline the streamed checks are read against. If this one fails,
    // nothing about HTTP is implicated - the platform cannot decode a WAV.
    private static void LocalFileIsExact(DecoderUnderTest subject)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flower-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var content = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
            var path = Path.Combine(directory, "check.wav");
            File.WriteAllBytes(path, content);

            var decoded = DecodeFully(subject, TrackAt(path, ShortTrack));

            AssertMatchesSource(content, decoded);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A temp directory that outlives the run is not a failed check.
            }
        }
    }

    // The headline. Same fixture, same oracle, fetched over a socket through
    // SeekableHttpStream instead of read off a disk - so a difference between
    // this and the local check is the streaming path and nothing else.
    private static void StreamedIsExact(DecoderUnderTest subject)
    {
        using var server = new LoopbackMediaServer();
        var content = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
        var url = server.Serve(content);

        var decoded = DecodeFully(subject, TrackAt(url, ShortTrack));

        AssertMatchesSource(content, decoded);
    }

    // A forward-only read is all a WAV needs, so refusing ranges must degrade
    // to "plays" rather than to "plays nothing" - the shape the original iOS
    // failure wore.
    private static void WithoutRangesStillDecodes(DecoderUnderTest subject)
    {
        using var server = new LoopbackMediaServer { ServesRanges = false };
        var content = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
        var url = server.Serve(content);

        var decoded = DecodeFully(subject, TrackAt(url, ShortTrack));

        AssertMatchesSource(content, decoded);
    }

    // Scrubbing a remote track has to work exactly as on a local one, and
    // "worked" means the audio after the seek is continuous - not that an
    // event fired. Stale pre-seek audio spliced onto the new position is a
    // real bug this shape has caught before, and it breaks the ramp.
    private static void SeekLandsInUnbrokenAudio(DecoderUnderTest subject)
    {
        using var server = new LoopbackMediaServer();
        var content = SyntheticWav.Build(SeekableTrack, SyntheticWav.Ramp());
        var url = server.Serve(content);

        // Smaller than the track on purpose, and nothing draining it yet.
        // A seek is only meaningful while a decode is in flight, and with a
        // ring big enough to hold the whole track the decoder finishes it -
        // in a fraction of a second, off a loopback socket - and the seek
        // arrives at a thread that has already exited. That is a race the
        // check loses only when the machine is busy, which is to say in the
        // full suite and not on its own. Held on backpressure instead, the
        // decoder is still there to be seeked, which is also the state it is
        // in during real playback.
        var ring = new GaplessRingBuffer(1024 * 1024);
        using var decoder = subject.Create(TrackAt(url, SeekableTrack), ring);

        var settled = new ManualResetEventSlim();
        var landedAt = -1L;
        decoder.SeekSettled += offset =>
        {
            landedAt = offset;
            settled.Set();
        };

        if (decoder.PrepareAsync().GetAwaiter().GetResult() != DecodePrepareResult.Ready)
            throw new CheckFailedException("the stream would not open");

        decoder.StartDecoding();

        if (!SpinFor(() => ring.TotalBytesWritten > 0, TimeSpan.FromSeconds(30)))
            throw new CheckFailedException("the decode never started");

        decoder.Seek(0.5f);

        if (!settled.Wait(TimeSpan.FromSeconds(30)))
            throw new CheckFailedException($"the seek never landed ({decoder.BytesProduced} bytes decoded)");

        // Only now, so everything it collects is post-seek by construction
        // rather than by the Drain noticing the ring's generation change.
        using var drain = new Drain(ring);

        SpinFor(() => drain.Collected > 2 * BytesPerSecond(), TimeSpan.FromSeconds(30));
        var after = InFixtureUnits(subject, drain.Snapshot());

        // SeekSettled carries an offset in the pipeline's own bytes, so it is
        // read back through the pipeline's own frame size rather than the
        // fixture's.
        var landedAtSeconds = landedAt
            / (double)(GaplessFormat.SampleRate * GaplessFormat.BytesPerSampleOf(subject.Format) * GaplessFormat.Channels);
        if (landedAtSeconds < 3.5 || landedAtSeconds > 6.5)
            throw new CheckFailedException($"a seek to the middle of a 10s track landed at {landedAtSeconds:F2}s");

        if (PcmOracle.IsSilent(after))
            throw new CheckFailedException($"{after.Length} bytes of silence after the seek");

        if (PcmOracle.RampBreak(after) is { } complaint)
            throw new CheckFailedException($"audio after the seek is not continuous: {complaint}");
    }

    // An unreachable server has to be a distinguishable answer, because the
    // coordinator responds to it differently from an unplayable file.
    private static void AbsentServerFailsPrepare(DecoderUnderTest subject)
    {
        var url = $"http://127.0.0.1:{LoopbackMediaServer.ClosedPort()}/rest/stream?id=abc";
        var ring = new GaplessRingBuffer(64 * 1024);
        using var decoder = subject.Create(TrackAt(url, ShortTrack), ring);

        var prepared = decoder.PrepareAsync().GetAwaiter().GetResult();

        if (prepared is not (DecodePrepareResult.Failed or DecodePrepareResult.TimedOut))
            throw new CheckFailedException($"a server that is not there prepared as {prepared}");
    }

    // A stream that dies mid-track is a failed track, not a finished one -
    // otherwise it ends quietly and collects a play count for audio nobody
    // heard.
    private static void CutStreamFaults(DecoderUnderTest subject)
    {
        using var server = new LoopbackMediaServer();
        var content = SyntheticWav.Build(SeekableTrack, SyntheticWav.Ramp());
        var url = server.Serve(content);
        server.CutBodyAt = content.Length / 4;

        var ring = new GaplessRingBuffer(8 * 1024 * 1024);
        using var decoder = subject.Create(TrackAt(url, SeekableTrack), ring);
        using var drain = new Drain(ring);

        var faulted = new ManualResetEventSlim();
        decoder.Faulted += () => faulted.Set();

        // Prepared first, deliberately. Starting a decoder that was never
        // prepared also faults, and a check that accepts that fault is
        // checking nothing: it passes without a byte of the stream having
        // been read. The cut is a quarter of the way in, so the open itself
        // is well clear of it and the fault under check is the one that
        // happens mid-decode.
        var prepared = decoder.PrepareAsync().GetAwaiter().GetResult();
        if (prepared != DecodePrepareResult.Ready)
            throw new CheckFailedException($"the track would not open before the cut: {prepared}");

        decoder.StartDecoding();

        if (!faulted.Wait(DecodeTimeout))
            throw new CheckFailedException("a stream cut mid-track should fault");

        // Settled first: the fault and the last write into the ring are on
        // the same thread, so a snapshot taken the instant Faulted fires can
        // legitimately race the drain thread and see nothing at all.
        drain.Settle(decoder.BytesProduced);

        var partial = InFixtureUnits(subject, drain.Snapshot());
        if (partial.Length == 0)
            throw new CheckFailedException($"no audio at all came out before the cut ({decoder.BytesProduced} bytes decoded)");

        // What did arrive still has to be the real thing. A fabricated tail -
        // what a decoder handed a clean end of stream tends to invent - reads
        // as silence or as a broken ramp.
        if (PcmOracle.IsSilent(partial))
            throw new CheckFailedException($"{partial.Length} bytes of silence before the cut");
    }

    private static Track TrackAt(string path, TimeSpan duration) => new()
    {
        Title = "A check track",
        Path = path,
        OriginFileExtension = "wav",
        Duration = duration,
    };

    // Bytes as the fixtures themselves hold them: 16-bit stereo at the
    // pipeline's rate. Everything decoded is narrowed to this before an
    // oracle sees it, so every length and offset in this file means the same
    // thing whatever canonical format the decoder under check pins the
    // pipeline to. See InFixtureUnits.
    private const int FixtureBytesPerFrame = 2 * (int)GaplessFormat.Channels;

    private static int BytesPerSecond() => (int)GaplessFormat.SampleRate * FixtureBytesPerFrame;

    // The same second, counted in the bytes the decoder under check actually
    // writes: FFmpeg's are half as many again as the fixture's. Used only for
    // measuring what the ring holds against what the decoder produced, which
    // is a question in the pipeline's units rather than the fixture's.
    private static int PipelineBytesPerSecond(DecoderUnderTest subject) =>
        (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerSampleOf(subject.Format) * (int)GaplessFormat.Channels;

    // Whatever the decoder delivered, in the fixture's units.
    //
    // Narrowing packed 24 back to 16 is exact here rather than approximate,
    // and that is a fact about the sources, not a tolerance: every fixture is
    // 16-bit, so FFmpeg's widening is a shift of exactly eight bits
    // (swresample scales S16 to S32 by 16, pack_s24 keeps the top three
    // bytes) and dropping the low byte undoes it. A byte-for-byte oracle
    // stays byte-for-byte, and a decoder that got the widening wrong fails it
    // rather than being quietly rounded into passing.
    private static byte[] InFixtureUnits(DecoderUnderTest subject, byte[] pcm)
    {
        if (subject.Format != PcmSampleFormat.S24)
            return pcm;

        var samples = pcm.Length / 3;
        var narrowed = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            narrowed[i * 2] = pcm[i * 3 + 1];
            narrowed[i * 2 + 1] = pcm[i * 3 + 2];
        }

        return narrowed;
    }

    // The fixture's own samples, which is what the decoder should hand back
    // byte for byte: SyntheticWav writes at the pipeline's rate and channel
    // count precisely so nothing legitimately alters them on the way.
    // The failure this check exists for: a server that is refusing requests
    // because its per-source budget is spent, not because anything is wrong
    // with the track.
    //
    // On Flower.Server that budget used to be shared across the whole /rest
    // surface, so an album grid's cover-art burst spent it and the next thing
    // refused was /rest/stream. The client treated 429 as an I/O error,
    // reopened three times - spending more of an exhausted budget - faulted
    // the decoder, and the queue skipped the track. Five of those in a row and
    // playback stopped altogether. An entire album vanished because some
    // pictures were being fetched, and nothing anywhere said "throttled".
    //
    // The budget is split by plane now and the client waits 429s out, but the
    // reason this is a check rather than only a unit test is that neither of
    // those is visible from a desktop suite: what has to still be true is that
    // the *audio arrives*, on the platform, after the wait. Held to the same
    // byte-for-byte oracle as an unthrottled stream, because being throttled
    // is not licence to return different audio.
    private static void ThrottledStreamStillDecodes(DecoderUnderTest subject, bool sendsRetryAfter)
    {
        using var server = new LoopbackMediaServer
        {
            // Enough refusals to be past any "retry three times" budget - the
            // shape that used to lose the track - and few enough that the
            // check is seconds. Retry-After of 1s, or none at all, in which
            // case the client falls back to its own first backoff.
            RefuseBodiesWith429 = 4,
            RetryAfterSeconds = sendsRetryAfter ? 1 : 0,
        };

        var wav = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
        var url = server.Serve(wav, "rest/stream?id=throttled");

        var decoded = DecodeFully(subject, new Track
        {
            Title = "Throttled",
            Path = url,
            OriginFileExtension = "wav",
            Duration = ShortTrack,
        });

        AssertMatchesSource(wav, decoded);
    }

    // The failure this check exists for, and the one that cost the most to
    // find: a track that is fetched with a credential good for a single
    // request.
    //
    // A streamed track's URL is signed once, when it is resolved, and then
    // fetched several times - the bytes=0-0 probe, the body GET, a reopen. The
    // nonce baked into it is single-use on the server (NonceReplayGuard), so
    // the probe spent it and the body GET was refused as a replay. Because the
    // probe went first the track had a correct length and no audio, and because
    // the refusal was Subsonic's HTTP 200 the client decoded the error message.
    //
    // Neither half is visible from a desktop suite, and it is worth being
    // precise about why: the desktop head plays local files, so it never
    // streams, and the loopback server here authenticated nothing, so it could
    // not express a replay at all. What has to be true, on the platform, is
    // that every request the pipeline makes carries its own fresh credential.
    private static void ReplayGuardedStreamStillDecodes(DecoderUnderTest subject)
    {
        using var server = new LoopbackMediaServer { RequiresFreshNonce = true };

        var wav = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
        var url = server.Serve(wav, "rest/stream?id=replay-guarded");

        var decoded = WithSigningCredentials(new FreshNonceCredentials(), () => DecodeFully(subject, new Track
        {
            Title = "Replay guarded",
            Path = url,
            OriginFileExtension = "wav",
            Duration = ShortTrack,
        }));

        AssertMatchesSource(wav, decoded);
    }

    // The other half, from the other side: when a request really is refused,
    // the refusal must not be mistaken for the track.
    //
    // Nothing signs here, so every request is refused - and the refusal is a
    // 200 carrying an error envelope, which is what the Subsonic protocol
    // mandates and what makes EnsureSuccessStatusCode useless on this surface.
    // A prepare that "succeeds" on 130 bytes of JSON is the bug; a prepare that
    // fails is the fix.
    private static void ProtocolErrorIsNotAudio(DecoderUnderTest subject)
    {
        using var server = new LoopbackMediaServer { RequiresFreshNonce = true };
        var url = server.Serve(SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp()), "rest/stream?id=refused");

        var ring = new GaplessRingBuffer(64 * 1024);
        using var decoder = subject.Create(TrackAt(url, ShortTrack), ring);

        var prepared = WithSigningCredentials(null, () => decoder.PrepareAsync().GetAwaiter().GetResult());

        if (prepared is not (DecodePrepareResult.Failed or DecodePrepareResult.TimedOut))
            throw new CheckFailedException($"a refused stream prepared as {prepared}");
    }

    // The audio pipeline's HttpClient is a static built once, and it reads its
    // credentials through PeerHttpClient.SigningCredentials on every request -
    // which is what lets a check swap them without rebuilding anything.
    private static T WithSigningCredentials<T>(IPeerCredentials? credentials, Func<T> body)
    {
        var original = PeerHttpClient.SigningCredentials;
        PeerHttpClient.SigningCredentials = () => credentials;
        try
        {
            return body();
        }
        finally
        {
            PeerHttpClient.SigningCredentials = original;
        }
    }

    // Enough of a credential for a server that only checks freshness. The real
    // one signs (SignedDeviceCredentials); what both have in common, and all
    // this check needs, is a nonce that is different every time.
    private sealed class FreshNonceCredentials : IPeerCredentials
    {
        public Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) =>
            Task.FromResult<IReadOnlyList<(string Key, string Value)>>(
            [
                ("X-Flower-Fingerprint", "device-checks"),
                ("X-Flower-Nonce", Guid.NewGuid().ToString("N")),
            ]);
    }

    private static void AssertMatchesSource(byte[] wav, byte[] decoded)
    {
        const int headerSize = 44;
        var expected = wav.AsSpan(headerSize);

        if (decoded.Length == 0)
            throw new CheckFailedException("no audio came out at all");

        if (PcmOracle.IsSilent(decoded))
            throw new CheckFailedException($"{decoded.Length} bytes of silence");

        var shared = Math.Min(expected.Length, decoded.Length);
        if (PcmOracle.Diff(expected[..shared], decoded.AsSpan(0, shared)) is { } complaint)
            throw new CheckFailedException(complaint);

        var missing = Math.Abs(expected.Length - decoded.Length);
        var tolerance = BytesPerSecond() * TailToleranceMs / 1000;
        if (missing > tolerance)
        {
            var direction = decoded.Length < expected.Length ? "short" : "long";
            throw new CheckFailedException($"{missing / (double)BytesPerSecond() * 1000:F0}ms {direction}: expected {expected.Length} bytes, got {decoded.Length}");
        }
    }

    // prepare: false is the golden path, not a variation on it.
    // GaplessCoordinator calls PrepareAsync only when it is decoding a track
    // ahead of the one playing; Play() - which is what pressing play reaches -
    // constructs a decoder and calls StartDecoding on it directly. Every check
    // here prepared first, so a decoder that could not start without one was
    // caught by none of them while failing every press of play.
    private static byte[] DecodeFully(DecoderUnderTest subject, Track track, bool prepare = true)
    {
        var ring = new GaplessRingBuffer(4 * 1024 * 1024);
        using var decoder = subject.Create(track, ring);
        using var drain = new Drain(ring);

        // Which of the two fired, not merely that one did. They used to share
        // a latch and nothing else, so a decode that broke mid-stream was
        // indistinguishable from one that ended cleanly and short: the fault
        // went unmentioned, the bytes that had arrived went to the length
        // oracle, and the check reported a tail that came up short. That
        // number was true and said nothing about why.
        var finished = new ManualResetEventSlim();
        var faulted = false;
        decoder.Drained += () => finished.Set();
        decoder.Faulted += () =>
        {
            faulted = true;
            finished.Set();
        };

        if (prepare)
        {
            var prepared = decoder.PrepareAsync().GetAwaiter().GetResult();
            if (prepared != DecodePrepareResult.Ready)
                throw new CheckFailedException($"the track would not open: {prepared}");
        }

        decoder.StartDecoding();

        if (!finished.Wait(DecodeTimeout))
            throw new CheckFailedException($"the decode never finished ({decoder.BytesProduced} bytes produced)");

        // Drained fires when the decoder stops producing, which can be a beat
        // before the last write lands in the ring.
        var produced = decoder.BytesProduced;
        drain.Settle(produced);
        var collected = drain.Snapshot();

        if (faulted)
            throw new CheckFailedException(
                $"the decode faulted after producing {produced} bytes, of which {collected.Length} arrived");

        // The third thing a short result can be, and the one that says the
        // decoder was fine: everything was decoded and the reader did not
        // collect it. Separated here so it can never again be handed to the
        // length oracle and reported as a track that ended early - the two
        // have entirely different causes and only one of them is about audio.
        var missing = produced - collected.Length;
        if (missing > PipelineBytesPerSecond(subject) * TailToleranceMs / 1000)
            throw new CheckFailedException(
                $"the drain collected {collected.Length} of the {produced} bytes the decoder produced");

        return InFixtureUnits(subject, collected);
    }

    // Pulls the ring dry on its own thread and keeps what came out. Without a
    // reader the decoder blocks on a full ring within a second of audio, so
    // this is not an observer - it is the other half of the pipeline.
    private sealed class Drain : IDisposable
    {
        private readonly GaplessRingBuffer _ring;
        private readonly Thread _thread;
        private readonly MemoryStream _collected = new();
        private readonly Lock _lock = new();

        private volatile bool _stopping;
        private int _generation;

        public Drain(GaplessRingBuffer ring)
        {
            _ring = ring;
            _generation = ring.Generation;
            _thread = new Thread(Pump) { IsBackground = true, Name = "flower-checks-drain" };
            _thread.Start();
        }

        public long Collected
        {
            get
            {
                lock (_lock)
                    return _collected.Length;
            }
        }

        private void Pump()
        {
            var buffer = new byte[64 * 1024];
            while (!_stopping)
            {
                var generation = _ring.Generation;
                if (generation != _generation)
                {
                    // A seek. Everything collected so far belongs to a
                    // position nobody is listening to any more, and keeping it
                    // would make the ramp look broken at the join.
                    lock (_lock)
                    {
                        _collected.SetLength(0);
                        _generation = generation;
                    }
                }

                var read = _ring.Read(buffer);
                if (read == 0)
                {
                    Thread.Sleep(1);
                    continue;
                }

                lock (_lock)
                {
                    if (_ring.Generation == _generation)
                        _collected.Write(buffer, 0, read);
                }
            }
        }

        // Waits until the drain has actually collected everything the decoder
        // says it produced, so a check does not read a ring the decoder has
        // finished writing but the reader has not finished emptying.
        //
        // Waiting for the collection to merely stop growing is what this used
        // to do, and it was wrong in a way that only showed up under load: two
        // samples 20ms apart look identical whether the ring is empty or the
        // drain thread was simply not scheduled in between, and a check that
        // gave up there read the audio still sitting in the ring as a track
        // that ended early. BytesProduced is the reader's own finish line and
        // is known exactly - the decoder counts it after each write - so it is
        // used instead of guessing from the outside.
        //
        // Quiescence stays as the fallback for the case that has no reachable
        // finish line: a decoder that faulted part-way through a write, whose
        // last counted bytes never made it into the ring. Reaching that
        // fallback is itself a finding, and DecodeFully reports the shortfall
        // rather than passing it on as a length.
        public void Settle(long produced)
        {
            var deadline = Stopwatch.StartNew();
            var last = -1L;
            var unchangedTicks = 0;

            while (deadline.Elapsed < SettlePatience)
            {
                var now = Collected;
                if (now >= produced)
                    return;

                if (now == last)
                {
                    if (++unchangedTicks >= 5)
                        return;
                }
                else
                {
                    unchangedTicks = 0;
                    last = now;
                }

                Thread.Sleep(10);
            }
        }

        public byte[] Snapshot()
        {
            lock (_lock)
                return _collected.ToArray();
        }

        public void Dispose()
        {
            _stopping = true;
            _thread.Join(TimeSpan.FromSeconds(5));
            _collected.Dispose();
        }
    }

    private static bool SpinFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            Thread.Sleep(10);
        }

        return false;
    }
}
