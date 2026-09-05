namespace Flower.Audio
{
    // How long an unbroken run of underrunning watchdog ticks lasted, and how
    // many underruns it accounted for.
    internal readonly record struct UnderrunRun(int Ticks, long Underruns);

    // Turns the render watchdog's underrun counter from a level into edges.
    //
    // The counter only ever climbs, so "it moved again this tick" is true for
    // every tick of a problem that has not been fixed - and the watchdog logged
    // one warning for each. That is the right shape for the mid-song glitch it
    // was written for, where the run is two or three ticks and every one of them
    // is evidence, and the wrong shape for anything that stays broken: an idle
    // phone whose feeder thread was polling a stopped device (see AudioFeeder)
    // wrote 86,400 identical warnings a day, which filled its log archive and
    // made every drain of that archive a rewrite of the whole retained week.
    //
    // So: one line when a run starts, one every SummaryTicks while it lasts, and
    // one when it ends. A run that ends is worth saying out loud because
    // "underruns stopped" is the half a log otherwise never records - the
    // absence of further warnings is equally what a crashed watchdog looks like.
    internal sealed class UnderrunRunTracker
    {
        // How often a run that will not end says so again: once a minute at the
        // watchdog's one-second tick. Often enough that a log covering a stuck
        // render still shows it, rare enough that it cannot drown one.
        public const int SummaryTicks = 60;

        private long _lastCount;
        private long _runStartCount;
        private int _runTicks;

        // What this tick's reading of the counter means: how many underruns it
        // added, whether it has earned a line, how many ticks the current run
        // has been going, and - on the tick a run ends - what that run came to.
        public readonly record struct Reading(long New, bool Log, int RunTicks, UnderrunRun? Cleared);

        public Reading Observe(long underrunCount)
        {
            var previousCount = _lastCount;
            var added = underrunCount - previousCount;
            _lastCount = underrunCount;

            if (added <= 0)
            {
                if (_runTicks == 0)
                    return new Reading(0, false, 0, null);

                var cleared = new UnderrunRun(_runTicks, underrunCount - _runStartCount);
                _runTicks = 0;
                return new Reading(0, false, 0, cleared);
            }

            // The count as it stood before this run's first tick, so both the
            // summary and the cleared line cover the whole run rather than
            // everything since the ring was created.
            if (_runTicks == 0)
                _runStartCount = previousCount;

            _runTicks++;

            // Counted from the run's own start, so a run that begins mid-minute
            // still reports a minute in rather than whenever the hour lines up.
            return new Reading(added, _runTicks == 1 || _runTicks % SummaryTicks == 0, _runTicks, null);
        }
    }
}
