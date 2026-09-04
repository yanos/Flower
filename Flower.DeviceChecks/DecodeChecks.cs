using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Flower.Audio;
using Flower.Models;
using Flower.Services;

using LibVLCSharp.Shared;

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

    // LibVLC's own resampler can trim or pad the very edges of a track by a
    // frame or two; a difference of more than this is a real one.
    private const int TailToleranceMs = 50;

    // MP3 and AAC both carry encoder delay and padding, and how much of it a
    // decoder hands back is a decision it is entitled to make. A quarter of a
    // second is well beyond any of that and well short of a missing verse.
    private const int LossyTailToleranceMs = 250;

    public static IReadOnlyList<CheckResult> RunAll()
    {
        VlcNativeSetup.Initialize();
        using var libVlc = new LibVLC();

        var results = new List<CheckResult>();

        // Every format a library actually holds, local and streamed, before
        // the WAV-only checks below go into the streaming path in detail.
        // A whole album of AAC playing silence while mp3 from the same server
        // played fine is the bug that put this loop here: the format was the
        // variable, and nothing was varying it.
        foreach (var fixture in Fixture.All)
        {
            results.Add(Run($"{fixture.Name} decodes from a local file", () => FixtureFromDisk(libVlc, fixture)));
            results.Add(Run($"{fixture.Name} decodes when streamed", () => FixtureFromServer(libVlc, fixture, servesRanges: true, answersHead: true)));
            results.Add(Run($"{fixture.Name} decodes when streamed from a server that refuses ranges", () => FixtureFromServer(libVlc, fixture, servesRanges: false, answersHead: true)));
            results.Add(Run($"{fixture.Name} decodes when streamed from a server that refuses HEAD", () => FixtureFromServer(libVlc, fixture, servesRanges: true, answersHead: false)));
        }

        results.AddRange(
        [
            Run("A local file decodes to exactly the PCM it holds", () => LocalFileIsExact(libVlc)),
            Run("A streamed track decodes to exactly the PCM the server holds", () => StreamedIsExact(libVlc)),
            Run("A server that refuses ranges still decodes", () => WithoutRangesStillDecodes(libVlc)),
            Run("A seek mid-stream lands in unbroken audio", () => SeekLandsInUnbrokenAudio(libVlc)),
            Run("A server that is not there reports a failed prepare", () => AbsentServerFailsPrepare(libVlc)),
            Run("A stream cut mid-track faults rather than ending quietly", () => CutStreamFaults(libVlc)),
            Run("A throttled server costs a wait, not the track", () => ThrottledStreamStillDecodes(libVlc, sendsRetryAfter: true)),
            Run("A throttled server that sends no Retry-After still costs only a wait", () => ThrottledStreamStillDecodes(libVlc, sendsRetryAfter: false)),
            Run("A server that requires a fresh nonce per request still decodes", () => ReplayGuardedStreamStillDecodes(libVlc)),
            Run("An error on an HTTP 200 is refused rather than decoded", () => ProtocolErrorIsNotAudio(libVlc)),
        ]);

        return results;
    }

    // One format, off a disk. The baseline each streamed result is read
    // against: a format that fails here fails everywhere, and HTTP is not
    // implicated.
    private static void FixtureFromDisk(LibVLC libVlc, Fixture fixture)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flower-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "check." + fixture.Extension);
            File.WriteAllBytes(path, fixture.Bytes());

            AssertPlayable(fixture, DecodeFully(libVlc, TrackFor(fixture, path)));
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
    private static void FixtureFromServer(LibVLC libVlc, Fixture fixture, bool servesRanges, bool answersHead)
    {
        using var server = new LoopbackMediaServer { ServesRanges = servesRanges, AnswersHead = answersHead };
        var url = server.Serve(fixture.Bytes(), "rest/stream?id=" + fixture.Extension);

        AssertPlayable(fixture, DecodeFully(libVlc, TrackFor(fixture, url)));
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
    private static void LocalFileIsExact(LibVLC libVlc)
    {
        var directory = Path.Combine(Path.GetTempPath(), "flower-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var content = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
            var path = Path.Combine(directory, "check.wav");
            File.WriteAllBytes(path, content);

            var decoded = DecodeFully(libVlc, TrackAt(path, ShortTrack));

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
    private static void StreamedIsExact(LibVLC libVlc)
    {
        using var server = new LoopbackMediaServer();
        var content = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
        var url = server.Serve(content);

        var decoded = DecodeFully(libVlc, TrackAt(url, ShortTrack));

        AssertMatchesSource(content, decoded);
    }

    // A forward-only read is all a WAV needs, so refusing ranges must degrade
    // to "plays" rather than to "plays nothing" - the shape the original iOS
    // failure wore.
    private static void WithoutRangesStillDecodes(LibVLC libVlc)
    {
        using var server = new LoopbackMediaServer { ServesRanges = false };
        var content = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
        var url = server.Serve(content);

        var decoded = DecodeFully(libVlc, TrackAt(url, ShortTrack));

        AssertMatchesSource(content, decoded);
    }

    // Scrubbing a remote track has to work exactly as on a local one, and
    // "worked" means the audio after the seek is continuous - not that an
    // event fired. Stale pre-seek audio spliced onto the new position is a
    // real bug this shape has caught before, and it breaks the ramp.
    private static void SeekLandsInUnbrokenAudio(LibVLC libVlc)
    {
        using var server = new LoopbackMediaServer();
        var content = SyntheticWav.Build(SeekableTrack, SyntheticWav.Ramp());
        var url = server.Serve(content);

        var ring = new GaplessRingBuffer(4 * 1024 * 1024);
        using var decoder = new TrackDecoder(libVlc, TrackAt(url, SeekableTrack), ring);

        var settled = new ManualResetEventSlim();
        var landedAt = -1L;
        decoder.SeekSettled += offset =>
        {
            landedAt = offset;
            settled.Set();
        };

        using var drain = new Drain(ring);

        if (decoder.PrepareAsync().GetAwaiter().GetResult() != DecodePrepareResult.Ready)
            throw new CheckFailedException("the stream would not open");

        decoder.StartDecoding();

        if (!SpinFor(() => decoder.BytesProduced > 0, TimeSpan.FromSeconds(30)))
            throw new CheckFailedException("the decode never started");

        decoder.Seek(0.5f);

        if (!settled.Wait(TimeSpan.FromSeconds(30)))
            throw new CheckFailedException("the seek never landed");

        // The ring is reset by the seek, and Drain throws away everything from
        // before the reset, so what it holds now is post-seek audio only.
        SpinFor(() => drain.Collected > 4 * BytesPerSecond(), TimeSpan.FromSeconds(30));
        var after = drain.Snapshot();

        var half = BytesPerSecond() * 5;
        if (landedAt < half * 0.7 || landedAt > half * 1.3)
            throw new CheckFailedException($"a seek to the middle of a 10s track landed at {landedAt / (double)BytesPerSecond():F2}s");

        if (PcmOracle.IsSilent(after))
            throw new CheckFailedException($"{after.Length} bytes of silence after the seek");

        if (PcmOracle.RampBreak(after) is { } complaint)
            throw new CheckFailedException($"audio after the seek is not continuous: {complaint}");
    }

    // An unreachable server has to be a distinguishable answer, because the
    // coordinator responds to it differently from an unplayable file.
    private static void AbsentServerFailsPrepare(LibVLC libVlc)
    {
        var url = $"http://127.0.0.1:{LoopbackMediaServer.ClosedPort()}/rest/stream?id=abc";
        var ring = new GaplessRingBuffer(64 * 1024);
        using var decoder = new TrackDecoder(libVlc, TrackAt(url, ShortTrack), ring);

        var prepared = decoder.PrepareAsync().GetAwaiter().GetResult();

        if (prepared is not (DecodePrepareResult.Failed or DecodePrepareResult.TimedOut))
            throw new CheckFailedException($"a server that is not there prepared as {prepared}");
    }

    // A stream that dies mid-track is a failed track, not a finished one -
    // otherwise it ends quietly and collects a play count for audio nobody
    // heard.
    private static void CutStreamFaults(LibVLC libVlc)
    {
        using var server = new LoopbackMediaServer();
        var content = SyntheticWav.Build(SeekableTrack, SyntheticWav.Ramp());
        var url = server.Serve(content);
        server.CutBodyAt = content.Length / 4;

        var ring = new GaplessRingBuffer(8 * 1024 * 1024);
        using var decoder = new TrackDecoder(libVlc, TrackAt(url, SeekableTrack), ring);
        using var drain = new Drain(ring);

        var faulted = new ManualResetEventSlim();
        decoder.Faulted += () => faulted.Set();

        decoder.StartDecoding();

        if (!faulted.Wait(DecodeTimeout))
            throw new CheckFailedException("a stream cut mid-track should fault");

        var partial = drain.Snapshot();
        if (partial.Length == 0)
            throw new CheckFailedException("no audio at all came out before the cut");

        // What did arrive still has to be the real thing. A fabricated tail -
        // LibVLC's own habit when it is handed a clean end of stream - reads
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

    private static int BytesPerSecond() => (int)GaplessFormat.SampleRate * GaplessFormat.BytesPerFrame;

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
    private static void ThrottledStreamStillDecodes(LibVLC libVlc, bool sendsRetryAfter)
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

        var decoded = DecodeFully(libVlc, new Track
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
    private static void ReplayGuardedStreamStillDecodes(LibVLC libVlc)
    {
        using var server = new LoopbackMediaServer { RequiresFreshNonce = true };

        var wav = SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp());
        var url = server.Serve(wav, "rest/stream?id=replay-guarded");

        var decoded = WithSigningCredentials(new FreshNonceCredentials(), () => DecodeFully(libVlc, new Track
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
    private static void ProtocolErrorIsNotAudio(LibVLC libVlc)
    {
        using var server = new LoopbackMediaServer { RequiresFreshNonce = true };
        var url = server.Serve(SyntheticWav.Build(ShortTrack, SyntheticWav.Ramp()), "rest/stream?id=refused");

        var ring = new GaplessRingBuffer(64 * 1024);
        using var decoder = new TrackDecoder(libVlc, TrackAt(url, ShortTrack), ring);

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

    private static byte[] DecodeFully(LibVLC libVlc, Track track)
    {
        var ring = new GaplessRingBuffer(4 * 1024 * 1024);
        using var decoder = new TrackDecoder(libVlc, track, ring);
        using var drain = new Drain(ring);

        var finished = new ManualResetEventSlim();
        decoder.Drained += () => finished.Set();
        decoder.Faulted += () => finished.Set();

        var prepared = decoder.PrepareAsync().GetAwaiter().GetResult();
        if (prepared != DecodePrepareResult.Ready)
            throw new CheckFailedException($"the track would not open: {prepared}");

        decoder.StartDecoding();

        if (!finished.Wait(DecodeTimeout))
            throw new CheckFailedException("the decode never finished");

        // Drained fires when the decoder stops producing, which can be a beat
        // before the last write lands in the ring.
        drain.Settle();
        return drain.Snapshot();
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

        // Waits for the ring to stop producing, so a check does not read a
        // buffer the decoder is still writing the tail of.
        public void Settle()
        {
            var last = -1L;
            for (var i = 0; i < 50; i++)
            {
                var now = Collected;
                if (now == last)
                    return;

                last = now;
                Thread.Sleep(20);
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
