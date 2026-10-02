using System;
using System.Diagnostics;

using Flower.Models;

namespace Flower.Services;

// How much of the playing track has actually been heard, which is what decides
// whether it counts as a play: CountedFraction of it, and not before.
//
// Heard, not reached. A play used to be counted when a track arrived at its
// end, which is a statement about the position and not about the listening:
// dragging the seek bar to the last ten seconds counted, and skipping on during
// the fade-out of a song heard the whole way through did not. So this adds up
// the time the position spent moving at the speed of the clock, and a jump -
// forwards or back, however it was made - adds nothing.
//
// A jump is told from playing by the clock rather than by being told about the
// seek: the position is set from the seek bar, the Lock Screen, Previous and
// the resume-on-start path, none of which pass through one place, and all of
// them move the position further than the time since the last report can
// explain. That also makes it indifferent to how often it is told the position
// - four times a second on a phone, once a second in a browser tab that has
// been put in the background.
//
// Driven by PlaylistControlViewModel, like AlbumProgressTracker and for the same
// reason. Locked because those calls come from three threads: the position
// timer's, the decoder's (a track ending) and the UI's (everything else).
public sealed class ListenMeter
{
    public const double CountedFraction = 0.9;

    // How much further than the clock the position may have moved and still be
    // playback. The position is counted in the blocks the output device takes,
    // so two reports a quarter of a second apart can be a little more than a
    // quarter of a second of audio apart. Generous, because the cost of being
    // wrong the other way is a seek of under a second counted as listening.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);

    private static readonly Stopwatch Running = Stopwatch.StartNew();

    private readonly Func<TimeSpan> _clock;
    private readonly object _lock = new();

    private Track? _track;
    private long _durationMs;
    private long _listenedMs;

    // The furthest position playback has been heard at - what the track's real
    // length is measured as when it ends, see Finish.
    private long _furthestMs;

    // The last report, which the next one is measured against. No time means
    // there is nothing to measure against: the next report only sets one.
    private long _lastTimeMs;
    private TimeSpan? _lastSeenAt;

    private bool _counted;

    public ListenMeter(Func<TimeSpan>? clock = null)
    {
        _clock = clock ?? (() => Running.Elapsed);
    }

    // A track starting, from the top - including the same one starting again
    // under repeat, which is a second play and is measured as one.
    public void Begin(Track track)
    {
        lock (_lock)
        {
            _track = track;
            _durationMs = (long)track.Duration.TotalMilliseconds;
            _listenedMs = 0;
            _furthestMs = 0;
            _lastTimeMs = 0;
            _lastSeenAt = _clock();
            _counted = false;
        }
    }

    // The track was started part-way through, at a place it was left at
    // earlier (Track.ResumePosition, or an album picked up from the Continue
    // Playing shelf). That is one listen in two sittings rather than a seek, so
    // what came before the place counts as heard - otherwise a podcast put down
    // half-way could never be counted at all. A place already over the line was
    // counted in the sitting that got it there.
    public void ResumedAt(TimeSpan position)
    {
        lock (_lock)
        {
            if (_track == null)
                return;

            var positionMs = (long)position.TotalMilliseconds;
            _listenedMs += positionMs;
            _furthestMs = Math.Max(_furthestMs, positionMs);
            _lastTimeMs = positionMs;
            _lastSeenAt = _clock();

            if (_durationMs > 0 && HeardEnoughOf(_durationMs))
                _counted = true;
        }
    }

    // Where playback has got to. Answers the track on the one report that takes
    // it over the line, and null on every other - so whoever asks counts each
    // play exactly once, and counts it while it is still playing rather than
    // on the way out, where a quit or a crash would lose it.
    public Track? Observe(long timeMs)
    {
        lock (_lock)
        {
            if (_track == null)
                return null;

            var now = _clock();
            if (_lastSeenAt is { } seenAt)
            {
                var moved = timeMs - _lastTimeMs;
                if (moved > 0 && moved <= (now - seenAt + Slack).TotalMilliseconds)
                {
                    _listenedMs += moved;
                    _furthestMs = Math.Max(_furthestMs, timeMs);
                }
            }

            _lastTimeMs = timeMs;
            _lastSeenAt = now;

            // Without a length there is nothing to be nine tenths of, and the
            // track ending is the only evidence left - see Finish.
            if (_counted || _durationMs <= 0 || !HeardEnoughOf(_durationMs))
                return null;

            _counted = true;
            return _track;
        }
    }

    // Playback was put down. Whatever the position does before it is picked up
    // again is not listening, however long the pause makes the clock say it
    // could have been - a seek made while paused is still a seek.
    public void Suspend()
    {
        lock (_lock)
            _lastSeenAt = null;
    }

    // The track played to its end. Answers whether that is a play still owed -
    // false for one already counted on the way, and for one that got here by
    // being seeked through.
    //
    // Measured against how far playback was heard to rather than the tagged
    // length, for two reasons. The end arrives when the decoder runs out, with
    // the last couple of seconds still buffered and unreported, which is most
    // of a track short enough. And a tag that overstates the length would
    // otherwise describe a track that can never be finished.
    public bool Finish(Track track)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_track, track))
                return false;

            _track = null;
            return !_counted && HeardEnoughOf(_furthestMs);
        }
    }

    // Nothing here for a track left any other way - skipped, stopped, the queue
    // emptied. None of those owes a play: one is counted when it crosses the
    // line, and the next Begin starts the measure over.
    private bool HeardEnoughOf(long lengthMs) => _listenedMs >= CountedFraction * lengthMs;
}
