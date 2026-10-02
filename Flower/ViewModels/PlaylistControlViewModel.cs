using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Audio;
using Flower.Logging;
using Flower.Services;
using Flower.Models;
using Flower.Persistence;

namespace Flower.ViewModels
{
    public class PlaylistControlViewModel : ViewModelBase, IDisposable
    {
        private readonly ILogger<PlaylistControlViewModel> _logger;

        private Playlist _currentPlaylist;
        private Track? _currentlyPlayingTrack;

        // Where CurrentlyPlayingTrack sits in _currentPlaylist, as a position
        // rather than a value to search for. Track equality is by Id (see
        // Track.Equals), so the same track queued twice - "add to playlist"
        // twice, or an album that repeats a song - is genuinely the same object
        // in two slots, and every IndexOf over the queue resolved to the first
        // of them: playing the second copy then advanced from the first, so
        // Next jumped backwards and auto-advance replayed a chunk of the queue.
        // -1 means "not known", which is every path that starts playback from a
        // bare Track (see Play(Track)) plus anything that invalidates the
        // position; ResolveQueueIndex falls back to IndexOf there, which is no
        // worse than what this replaced. See docs/ARCHITECTURE-REVIEW.md 0.2.
        private int _queueIndex = -1;

        // Bumped by every Play. A start deferred while its stream URL is minted
        // (see StartWhenResolved) carries the generation it was requested at and
        // gives up if anything has been started since - otherwise a URL that
        // took two seconds to arrive would hijack playback from whatever the
        // user asked for in the meantime.
        private int _playGeneration;
        private Track? _selectedTrack;
        private bool _isRepeatEnabled;
        private bool _isShuffleEnabled;
        private readonly Random _random = new();
        private readonly Library _library;

        // ── Per-track playback options (see Track's own section of the same
        //    name, and Track Info's Options tab where they are set) ──────────
        //
        // Which track _lastKnownTimeMs belongs to - deliberately not
        // CurrentlyPlayingTrack. Starting a new track can make the old one
        // raise Stopped *after* CurrentlyPlayingTrack has already moved on, and
        // saving the outgoing track's elapsed time onto the incoming one would
        // resume every track at the position of the one before it. Null means
        // "nothing to remember", which is both the normal case (the option is
        // off) and the state between tracks.
        private Track? _positionTrack;
        private long _lastKnownTimeMs;

        // Where to seek once the incoming track actually starts. Applied on the
        // Playing event rather than straight after Play(): at that moment the
        // decoder has not reported a length yet, and a seek expressed as a
        // fraction of an unknown length is a seek to nowhere.
        private TimeSpan? _pendingSeek;

        // How much of the playing track has been heard, which is what a play
        // count is counted on - see ListenMeter.
        private readonly ListenMeter _listen;

        private readonly AppSettings _appSettings;
        private readonly AppSettingsStore _appSettingsStore;

        private IAudioManager _audioManager { get; }

        // Loops the currently playing track instead of advancing when it ends.
        // Only applies to natural end-of-track auto-advance; manual Next()/Previous() still move.
        public bool IsRepeatEnabled
        {
            get => _isRepeatEnabled;
            set
            {
                _isRepeatEnabled = value;
                OnPropertyChanged();
            }
        }

        // Picks a random track (instead of the next one in order) whenever the
        // queue advances, whether that's auto-advance on end-of-track or a manual Next().
        public bool IsShuffleEnabled
        {
            get => _isShuffleEnabled;
            set
            {
                _isShuffleEnabled = value;
                OnPropertyChanged();
            }
        }

        public Track? SelectedTrack
        { 
            get => _selectedTrack;
            set
            { 
                _selectedTrack = value;
                OnPropertyChanged();
            }
        }

        public Track? CurrentlyPlayingTrack 
        { 
            get => _currentlyPlayingTrack;
            private set
            {
                _currentlyPlayingTrack = value;
                OnPropertyChanged();
            }
        }

        public bool IsPlaying => _audioManager.IsPlaying;

        public bool CanResume => CurrentlyPlayingTrack != null;

        private readonly IStreamUrlResolver? _streamUrlResolver;

        public PlaylistControlViewModel(
            IAudioManager audioManager,
            MainPlaylist playlist,
            Library library,
            AppSettings appSettings,
            AppSettingsStore appSettingsStore,
            ILogger<PlaylistControlViewModel> logger,
            // Nullable and defaulted for the same reason MainViewModel's peer
            // dependencies are: the browser head registers no peer stack at all
            // (see App.axaml.cs), and a container cannot inject what is not
            // registered. Null simply means placeholders cannot be played here.
            IStreamUrlResolver? streamUrlResolver = null,
            // Defaulted to one of its own over no store, so a test that builds
            // this class directly still has a shelf to read - it just is not
            // written anywhere.
            AlbumProgressTracker? albumProgress = null,
            // Only ever passed by a test, which needs to own its clock.
            ListenMeter? listenMeter = null)
        {
            _streamUrlResolver = streamUrlResolver;
            AlbumProgress = albumProgress ?? new AlbumProgressTracker(library);
            _listen = listenMeter ?? new ListenMeter();
            _audioManager = audioManager;
            _currentPlaylist = playlist;
            _library = library;
            _appSettings = appSettings;
            _appSettingsStore = appSettingsStore;
            _logger = logger;
            _isRepeatEnabled = appSettings.IsRepeatEnabled;
            _isShuffleEnabled = appSettings.IsShuffleEnabled;

            _subscriptions.Add<EventHandler>((s, e) =>
            {
                ApplyPendingSeek();
                OnPropertyChanged(nameof(IsPlaying));
            },
                h => _audioManager.Playing += h, h => _audioManager.Playing -= h);

            // The only source of "how far into this track are we" this class
            // has: Time is a live counter on the audio manager and is gone the
            // moment playback stops, so it has to be latched while it is still
            // being reported.
            _subscriptions.Add<EventHandler>((s, e) =>
            {
                var time = _audioManager.Time;
                if (_positionTrack != null)
                    _lastKnownTimeMs = time;
                AlbumProgress.UpdatePosition(time);

                // The play is counted here, the moment enough of the track
                // has been heard, rather than when it is left - so skipping on
                // through a fade-out, stopping, or quitting does not lose it.
                if (_listen.Observe(time) is { } heard)
                    CountPlay(heard);
            },
                h => _audioManager.PositionChanged += h, h => _audioManager.PositionChanged -= h);

            // Posted, not run inline: a stop is usually a UI gesture, but not
            // always - MiniaudioSink raises it from HandleUnexpectedStop when
            // the output device dies, on a backend thread - and everything
            // here mutates observable state the view is bound to.
            _subscriptions.Add<EventHandler>((s, e) => Dispatcher.UIThread.Post(() =>
            {
                LeavePlayingTrack();
                AlbumProgress.Flush();
                _parkedAtQueueTop = false;
                OnPropertyChanged(nameof(IsPlaying));
                CurrentlyPlayingTrack = null;
            }),
                h => _audioManager.Stopped += h, h => _audioManager.Stopped -= h);

            // The queue ran out and the last track's audio has been heard - see
            // IAudioManager.PlayedOut, which is raised on the UI thread after
            // the manager has put its own output down. Not posted, unlike
            // Stopped above: this one arrives by contract on the thread that
            // owns the state it is about to change.
            _subscriptions.Add<EventHandler>((s, e) => ParkAtTopOfQueue(),
                h => _audioManager.PlayedOut += h, h => _audioManager.PlayedOut -= h);

            // Remembered but not forgotten: a pause is the most likely moment
            // for a long file to be put down for the day, and unlike Stopped
            // this leaves _positionTrack in place so resuming carries straight
            // on and a later stop still has something to save.
            _subscriptions.Add<EventHandler>((s, e) =>
            {
                SaveResumePosition();
                AlbumProgress.Flush();
                _listen.Suspend();
                OnPropertyChanged(nameof(IsPlaying));
            },
                h => _audioManager.Paused += h, h => _audioManager.Paused -= h);

            // This handler runs on the LibVLC decode callback thread, at the
            // exact moment the gapless seam is open: the finished track's
            // decode is exhausted and only what is left in the shared ring is
            // still covering the render callback. Anything slow here is heard.
            // So the queue decision - the only part the advance depends on -
            // stays inline, and the bookkeeping (a play-count UPDATE that
            // takes Library's lock, which a background rescan can be holding
            // for a while, plus a possible resume-position write) is handed to
            // the pool. See GaplessCoordinator.HandleDrainedOrFaulted, which
            // primes the ring before raising this for the same reason.
            _subscriptions.Add<EventHandler>((s, e) =>
            {
                if (CurrentlyPlayingTrack != null)
                {
                    var finishedTrack = CurrentlyPlayingTrack;
                    _logger.LogDebug("EndReached: {Title} ({Path})", finishedTrack.Title, LogPath.Short(finishedTrack.Path));

                    // Reaching the end is proof that playback works, so a run
                    // of failures before it was a run of bad files rather than
                    // a bad source - see MaxConsecutiveFailures. A track that
                    // produced no audio does not arrive here at all; the
                    // coordinator sends that one to TrackFailed instead.
                    _consecutiveFailures = 0;

                    // finishedTrack can be a stale reference: every launch kicks off a
                    // background rescan (see App.axaml.cs) that replaces _library.Tracks
                    // wholesale with brand-new Track instances, even for files that didn't
                    // change. If that rescan lands while this track is still playing (easily
                    // enough time if the user alt-tabs away for a bit - confirmed via a real
                    // repro), CurrentlyPlayingTrack still points at the old, now-orphaned
                    // object. IncrementPlayCount resolves the current object and applies the
                    // increment atomically under Library's own lock, so a rescan racing on
                    // another thread (EndReached fires on a LibVLC callback thread, the
                    // rescan runs on a threadpool thread - see Library._lock) can't land
                    // between "resolve" and "increment" and silently discard it the way a
                    // plain find-then-increment here already proved it could.
                    // Reaching the end is the one way of leaving a track that
                    // must NOT be remembered: a podcast listened to the whole
                    // way through should start at the top next time, not at its
                    // final second. The in-memory half is cleared here, before
                    // the advance below, so the outgoing track is dealt with
                    // while it is still the one _positionTrack names; the
                    // store write goes with the rest of the bookkeeping.
                    var hadResumePosition = finishedTrack.ResumePosition != null;
                    _positionTrack = null;
                    _lastKnownTimeMs = 0;

                    // Reaching the end is not by itself a play - a track seeked
                    // through gets here too - and a long one heard properly was
                    // already counted at nine tenths. What is left for this to
                    // say yes to is a track too short, or too badly tagged, to
                    // have crossed that line before its decoder ran out.
                    var playOwed = _listen.Finish(finishedTrack);

                    var next = GetUpcomingEntry(finishedTrack, ResolveQueueIndex(finishedTrack));
                    if (next.Track != null)
                    {
                        _logger.LogDebug("Auto-advancing to {Title} (repeat={Repeat}, shuffle={Shuffle})", next.Track.Title, IsRepeatEnabled, IsShuffleEnabled);

                        // immediate: false - the queue advancing, not the
                        // user skipping, so whatever the finished track still
                        // has buffered gets played out rather than cut off.
                        Dispatcher.UIThread.Post(() => Play(next.Track, next.Index, immediate: false));
                    }

                    // IncrementPlayCount raises Library.TrackChanged as a
                    // PlayFinished and persists the new count itself - a row
                    // refresh, not the full UI rebuild plus peer library sync
                    // a library change means. See ARCHITECTURE-REVIEW Tier 1.1.
                    // Caught rather than propagated: this is bookkeeping
                    // running on a pool thread with nobody to hand an
                    // exception to, where an unobserved one takes the process
                    // down.
                    OffPlaybackThread(() =>
                    {
                        try
                        {
                            if (playOwed)
                                _library.IncrementPlayCount(finishedTrack);

                            // Unconditionally once there is one, not only when
                            // the option is on: turning the option off should
                            // not leave a stale position behind to be honoured
                            // if it is ever turned back on.
                            if (hadResumePosition)
                                _library.RecordResumePosition(finishedTrack, null);

                            // Off this thread too, since it can mean grouping
                            // the library into albums. Racing the next song's
                            // start is harmless - see TrackFinished.
                            AlbumProgress.TrackFinished(finishedTrack);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Post-playback bookkeeping failed for {Path}", LogPath.Short(finishedTrack.Path));
                        }
                    });
                }
            },
                h => _audioManager.EndReached += h, h => _audioManager.EndReached -= h);

            // A track that couldn't be decoded (corrupt file, unsupported
            // format, unreadable path) used to arrive on EndReached like any
            // finished track, so it picked up a PlayCount on its way past and
            // was indistinguishable from one the user actually listened to.
            // Advance the same way, but count nothing and don't stamp
            // LastPlayedAt - and re-raise for whoever wants to tell the user.
            _subscriptions.Add<EventHandler<TrackFailedEventArgs>>((_, e) =>
            {
                _logger.LogWarning("Skipping {Title} ({Path}) - it could not be played", e.Track.Title, LogPath.Short(e.Track.Path));
                PlaybackFailed?.Invoke(this, e);

                if (++_consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _logger.LogWarning(
                        "Stopping after {Count} tracks in a row that could not be played - at that point the problem is the source, not any one track",
                        _consecutiveFailures);
                    return;
                }

                // Repeat would re-attempt the same broken file forever, so the
                // next track here is always the *next* one, never a repeat of
                // this one.
                var next = GetNextEntry(e.Track, ResolveQueueIndex(e.Track));
                if (next.Track != null && next.Track != e.Track)
                    Dispatcher.UIThread.Post(() => Play(next.Track, next.Index, immediate: false));
            },
                h => _audioManager.TrackFailed += h, h => _audioManager.TrackFailed -= h);
        }

        // Skipping past a track that won't play is the right thing to do once.
        // Doing it forever is what a broken *source* looks like from in here:
        // an unreachable server, a disconnected drive, a container LibVLC
        // cannot open. Every track fails the same way, each failure advances to
        // the next, and the queue empties itself at two tracks a second without
        // a sound - fifteen tracks in twenty seconds, then round again, because
        // repeat put it back at the top.
        //
        // One count and one limit rather than a rule per mode. Repeat-one,
        // repeat-all, shuffle and no repeat at all differ only in *which* track
        // comes next, and none of them has an answer for "none of them work";
        // asking each mode when to give up would be four ways of writing five.
        // Five is enough to be sure it is not one bad file, few enough that
        // nobody sits through it, and short enough that the notification each
        // failure raises still reads as a handful rather than a flood.
        private const int MaxConsecutiveFailures = 5;

        // Reset by anything that proves playback works: a track that reaches
        // its end with audio behind it, and any track the user starts by hand.
        // Not reset by the automatic advance itself, which is the run being
        // counted.
        private int _consecutiveFailures;

        // How work that must not run on the LibVLC decode callback thread gets
        // off it - see the EndReached handler above for why. Settable so a
        // test can run it inline and assert straight after raising the event
        // instead of racing a threadpool item; nothing in the app changes it.
        public Action<Action> OffPlaybackThread { get; set; } = work => Task.Run(work);

        // Enough of the track has been heard, part-way through it. Handed off
        // like the end-of-track bookkeeping and for a version of the same
        // reason: this is the position timer's thread, every other subscriber
        // to PositionChanged is queued behind this one, and Library's lock is
        // something a rescan can hold for a while - the seek bar should not
        // freeze over a play count.
        private void CountPlay(Track track)
        {
            OffPlaybackThread(() =>
            {
                try
                {
                    _library.IncrementPlayCount(track);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Counting a play failed for {Path}", LogPath.Short(track.Path));
                }
            });
        }

        // Every event this class attaches to in its constructor, paired with
        // its teardown - see SubscriptionBag, and docs/ARCHITECTURE-REVIEW.md
        // Tier 2.3.
        private readonly SubscriptionBag _subscriptions = new();

        // A singleton in the app, so in practice this runs at process exit and
        // never matters. It exists so a test can build one, use it, and let go
        // without leaving five handlers attached to a shared IAudioManager -
        // which is exactly how one test's ViewModel used to keep reacting to
        // the next test's playback events.
        public void Dispose() => _subscriptions.Dispose();

        // Surfaced for the UI to show a "couldn't play this" message. Nothing
        // consumes it yet - see docs/ARCHITECTURE-REVIEW.md - so today the Log
        // window is where a failed track shows up.
        public event EventHandler<TrackFailedEventArgs>? PlaybackFailed;

        // The Home screen's Continue Playing shelf - which albums are part-way
        // through, and where. Fed from here because every start, end and pause
        // already passes through this class.
        public AlbumProgressTracker AlbumProgress { get; }

        // The queue Next/Previous/auto-advance walk. Exposed read-only so a
        // test can assert what a view actually anchored it to - see
        // MainViewModel.SetPlayQueue.
        public Playlist CurrentPlaylist => _currentPlaylist;

        public void SetCurrentPlaylist(Playlist playlist)
        {
            _currentPlaylist = playlist;

            // A position into the old queue means nothing in the new one. Every
            // caller re-anchors the queue immediately before starting a track,
            // so this is normally overwritten by the Play that follows; when it
            // isn't (the queue changed under a track that keeps playing),
            // ResolveQueueIndex searches the new list instead.
            _queueIndex = -1;
            _linedUpCount = 0;
            OnPropertyChanged(nameof(CurrentPlaylist));
        }

        // The slot CurrentlyPlayingTrack occupies in the queue, or -1 when it
        // isn't in there at all. Exposed so a test can assert that playing the
        // second of two identical entries actually anchors to the second.
        public int QueueIndex => ResolveQueueIndex(CurrentlyPlayingTrack);

        // Trusts the remembered position only while it still holds the track it
        // was recorded for - the queue can be replaced wholesale (a rescan
        // rebinding instances, SetCurrentPlaylist, a playlist edit) between the
        // Play that recorded it and the advance that reads it.
        private int ResolveQueueIndex(Track? track)
        {
            if (track == null)
                return -1;

            var tracks = _currentPlaylist.Tracks;
            if (_queueIndex >= 0 && _queueIndex < tracks.Count && tracks[_queueIndex] == track)
                return _queueIndex;

            return IndexOfInQueue(track);
        }

        // Playlist.Tracks is an IReadOnlyList, which has no IndexOf of its own.
        private int IndexOfInQueue(Track track)
        {
            var tracks = _currentPlaylist.Tracks;
            for (var i = 0; i < tracks.Count; i++)
            {
                if (tracks[i] == track)
                    return i;
            }

            return -1;
        }

        // ── Lining songs up ───────────────────────────────────────────────
        //
        // Play Next and Add to Queue: songs put into the queue that is playing
        // rather than replacing it. The queue is replaced whole on every
        // change rather than edited in place, because it is not always a list
        // of the queue's own - before anything has played it is MainPlaylist,
        // the library itself - and a Playlist's Tracks are copy-on-write
        // anyway (see Playlist._tracks).

        // How many entries straight after the playing one were put there by
        // Play Next or Add to Queue - the songs the user lined up, which play
        // in the order they were lined up even under shuffle. Without this,
        // Play Next with shuffle on put a song after the current one in a
        // list shuffle never walks in order, and the next song was still a
        // roll of the dice. Shrinks by one as each of them starts, and is
        // forgotten on a jump elsewhere or a new queue.
        private int _linedUpCount;

        /// <summary>The number of entries after the playing one that were lined up
        /// by hand and play next in order, shuffle or not.</summary>
        public int LinedUpCount => _linedUpCount;

        /// <summary>Puts <paramref name="tracks"/> straight after the song playing,
        /// ahead of anything lined up before them.</summary>
        public void PlayNext(IReadOnlyList<Track> tracks) => LineUp(tracks, next: true);

        /// <summary>Puts <paramref name="tracks"/> at the end of the queue - or,
        /// under shuffle, where the queue has no end to speak of, after whatever
        /// else has been lined up.</summary>
        public void AddToQueue(IReadOnlyList<Track> tracks) => LineUp(tracks, next: false);

        private void LineUp(IReadOnlyList<Track> tracks, bool next)
        {
            if (tracks.Count == 0)
                return;

            // Nothing playing: there is nothing for these to come after, and
            // the queue as it stands was never lined up by anyone - so they
            // become the queue, and start.
            if (CurrentlyPlayingTrack is not { } current)
            {
                SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track>(tracks)));
                Play(tracks[0], 0);
                return;
            }

            var queue = new List<Track>(_currentPlaylist.Tracks);
            var index = ResolveQueueIndex(current);

            // Playing something the queue does not hold (the queue was replaced
            // under it): the song playing is put back at the head of a queue of
            // its own, so there is a place to come after.
            if (index < 0)
            {
                queue = [current];
                index = 0;
                _linedUpCount = 0;
            }

            int at;
            if (next)
                at = index + 1;
            else if (IsShuffleEnabled)
                at = index + 1 + _linedUpCount;
            else
                at = queue.Count;

            queue.InsertRange(at, tracks);
            if (at <= index + 1 + _linedUpCount)
                _linedUpCount += tracks.Count;

            _logger.LogInformation("Lined up {Count} songs to play {Where}", tracks.Count, next ? "next" : "last");
            ReplaceQueueKeepingPlace(queue, index);
        }

        /// <summary>Moves the entry at <paramref name="from"/> to sit before the
        /// one at <paramref name="insertBefore"/>, or at the end when that is the
        /// queue's length - both as slots in <see cref="CurrentPlaylist"/>, since
        /// a queue can hold one song twice. The song playing stays where it is,
        /// and nothing moves in front of it.</summary>
        public void MoveQueueEntry(int from, int insertBefore)
        {
            var queue = new List<Track>(_currentPlaylist.Tracks);
            var index = QueueIndex;
            if (from < 0 || from >= queue.Count || from == index)
                return;
            if (insertBefore <= index || insertBefore > queue.Count)
                return;
            if (insertBefore == from || insertBefore == from + 1)
                return;

            // The lined-up block keeps its own length as songs cross into or out
            // of it - see _linedUpCount.
            var blockEnd = index + 1 + _linedUpCount;
            var wasInBlock = index >= 0 && from > index && from < blockEnd;
            // Dropped right after the last of them is still among them for a
            // song that was, and just after them for one that was not.
            var landsInBlock = index >= 0 && (insertBefore < blockEnd || (wasInBlock && insertBefore == blockEnd));
            if (wasInBlock && !landsInBlock)
                _linedUpCount--;
            else if (!wasInBlock && landsInBlock)
                _linedUpCount++;

            var track = queue[from];
            queue.RemoveAt(from);
            var target = insertBefore > from ? insertBefore - 1 : insertBefore;
            queue.Insert(target, track);

            // Only something moved from before the playing song to after it
            // shifts it - a move behind it, or between two songs after it,
            // leaves its slot alone.
            if (index >= 0 && from < index)
                index--;

            ReplaceQueueKeepingPlace(queue, index);
        }

        /// <summary>Takes the entry at <paramref name="slot"/> out of the queue.
        /// Not the song playing, which is left to Next or Stop.</summary>
        public void RemoveQueueEntry(int slot)
        {
            var queue = new List<Track>(_currentPlaylist.Tracks);
            var index = QueueIndex;
            if (slot < 0 || slot >= queue.Count || slot == index)
                return;

            if (index >= 0 && slot > index && slot <= index + _linedUpCount)
                _linedUpCount--;
            queue.RemoveAt(slot);
            if (index >= 0 && slot < index)
                index--;

            ReplaceQueueKeepingPlace(queue, index);
        }

        // A new queue with the song playing still at its own slot in it, and
        // the song after it armed again: what plays next is exactly what an
        // edit here changes, and the gapless pipeline has already decoded ahead
        // into whatever it was before.
        private void ReplaceQueueKeepingPlace(List<Track> queue, int index)
        {
            // Not SetCurrentPlaylist, which forgets the place: the change is
            // announced with the place already kept, so whatever rebuilds off
            // it finds the playing song at its own slot rather than at the
            // first copy of it.
            _currentPlaylist = new Playlist("Now Playing Queue", queue);
            _queueIndex = index;
            OnPropertyChanged(nameof(LinedUpCount));
            OnPropertyChanged(nameof(CurrentPlaylist));

            if (CurrentlyPlayingTrack is { } current && !_parkedAtQueueTop)
                ArmUpcoming(GetUpcomingEntry(current, _queueIndex).Track);
        }

        public void ToggleRepeat()
        {
            IsRepeatEnabled = !IsRepeatEnabled;
            _logger.LogInformation("Repeat {State}", IsRepeatEnabled ? "enabled" : "disabled");
            _appSettings.IsRepeatEnabled = IsRepeatEnabled;
            _ = _appSettingsStore.SaveAsync(_appSettings);

            // Repeat/shuffle change what "upcoming" resolves to, so a
            // gapless IAudioManager needs to hear about it even though the
            // currently playing track itself isn't changing.
            if (CurrentlyPlayingTrack is { } currentTrack)
                ArmUpcoming(GetUpcomingEntry(currentTrack, ResolveQueueIndex(currentTrack)).Track);
        }

        public void ToggleShuffle()
        {
            IsShuffleEnabled = !IsShuffleEnabled;
            _logger.LogInformation("Shuffle {State}", IsShuffleEnabled ? "enabled" : "disabled");
            _appSettings.IsShuffleEnabled = IsShuffleEnabled;
            _ = _appSettingsStore.SaveAsync(_appSettings);

            if (CurrentlyPlayingTrack is { } currentTrack)
                ArmUpcoming(GetUpcomingEntry(currentTrack, ResolveQueueIndex(currentTrack)).Track);
        }

        // What should play after the entry at currentIndex, given the current
        // repeat/shuffle state - carrying the position along with the track so
        // the advance lands on a slot rather than on the first entry that
        // happens to hold the same track.
        //
        // Deliberately does not wrap: an album that has played its last track is
        // over, and the queue advancing on its own past the end and starting it
        // again is a loop nobody asked for - that is what repeat is for. Manual
        // Next() still wraps (it goes through GetNextEntry directly), because a
        // press of the skip button on the last track is a request for
        // *something*, and there is nowhere else for it to go.
        //
        // Answering (null, -1) here is also what arms nothing behind the last
        // track, which is what eventually lets the audio manager notice it has
        // played out - see IAudioManager.PlayedOut and ParkAtTopOfQueue below.
        private (Track? Track, int Index) GetUpcomingEntry(Track currentTrack, int currentIndex) =>
            IsRepeatEnabled ? (currentTrack, currentIndex) : GetNextEntry(currentTrack, currentIndex, wrap: false);

        private (Track? Track, int Index) GetNextEntry(Track currentTrack, int currentIndex, bool wrap = true)
        {
            var tracks = _currentPlaylist.Tracks;
            if (tracks.Count == 0)
                return (null, -1);

            // What was lined up by hand plays in the order it was lined up,
            // shuffle or not - see _linedUpCount.
            if (IsShuffleEnabled && _linedUpCount > 0 && currentIndex >= 0 && currentIndex + 1 < tracks.Count)
                return (tracks[currentIndex + 1], currentIndex + 1);

            if (IsShuffleEnabled && tracks.Count > 1)
            {
                // Re-rolls on the current *slot*, not the current track: with
                // duplicates in the queue, excluding by value would refuse to
                // shuffle into the other copy, and a queue of nothing but
                // copies of one track would spin here forever.
                //
                // Picked from a candidate list rather than by re-rolling until
                // something acceptable comes up, because Track.IgnoreWhenShuffling
                // can exclude almost the whole queue and rejection sampling
                // against a 1-in-500 hit rate is a loop with no bound worth
                // trusting on the UI thread.
                var candidates = ShuffleCandidates(tracks, currentIndex);
                if (candidates.Count > 0)
                {
                    var index = candidates[_random.Next(candidates.Count)];
                    return (tracks[index], index);
                }

                // Every other slot is marked "ignore when shuffling" - which
                // makes the marks meaningless here, since the alternative is
                // refusing to advance at all. Fall back to an unrestricted pick.
                int any;
                do
                {
                    any = _random.Next(tracks.Count);
                } while (any == currentIndex);
                return (tracks[any], any);
            }

            // Off the end of a queue nobody asked to loop - the end of the
            // album, and the one case that answers "nothing".
            if (!wrap && currentIndex >= 0 && currentIndex + 1 >= tracks.Count)
                return (null, -1);

            // Off the end, or playing something that isn't in this queue at
            // all, both wrap round to the front - the behaviour the old
            // Playlist.GetNextTrack had, moved here with it.
            var next = currentIndex < 0 || currentIndex + 1 >= tracks.Count ? 0 : currentIndex + 1;
            return (tracks[next], next);
        }

        private (Track? Track, int Index) GetPreviousEntry(int currentIndex)
        {
            var tracks = _currentPlaylist.Tracks;
            if (tracks.Count == 0)
                return (null, -1);

            // Previous from the first entry stays on the first entry, and an
            // unknown position starts there too - deliberately not wrapping
            // backwards to the end the way Next wraps forwards.
            var previous = currentIndex <= 0 ? 0 : currentIndex - 1;
            return (tracks[previous], previous);
        }

        public void PlayOrPause()
        {
            var trackToPlay = SelectedTrack ?? _currentPlaylist.Tracks.FirstOrDefault();

            if (trackToPlay != null)
            {
                PlayOrPause(trackToPlay);
            }
        }

        // Starts a track whose position in the queue the caller doesn't know -
        // the position is searched for, so a duplicated track resolves to its
        // first copy. Prefer the overload below wherever the caller activated a
        // specific row and therefore does know.
        public void Play(Track track) => Play(track, -1);

        // queueIndex is where in CurrentPlaylist this track was activated from,
        // or -1 for "work it out". It is validated rather than trusted: callers
        // hand over an index into the list they were displaying, which is only
        // the queue because they re-anchored it immediately beforehand.
        // immediate says whether this start came from a user gesture or from
        // the queue advancing on its own - see IAudioManager.Play. Every
        // public caller is a gesture, so it defaults that way and only the
        // auto-advance handlers pass false.
        //
        // startAt starts the track part-way through rather than at the top -
        // Continue Playing picking an album up where it was left.
        public void Play(Track track, int queueIndex, bool immediate = true, TimeSpan? startAt = null)
        {
            // A gesture is the user saying "try again", so it starts the count
            // of consecutive failures over - otherwise a source that came back
            // (the server reachable again, the drive remounted) would still be
            // five failures deep and give up on the first stumble.
            if (immediate)
                _consecutiveFailures = 0;

            // Worked out against the track as queued - the placeholder - since
            // that is what the queue holds. ResolveForPlayback's copy keeps
            // Track.Id, so this stays correct either way, but doing it first
            // makes that independent of the copy's behaviour.
            var previousIndex = _queueIndex;
            _queueIndex = queueIndex >= 0 && queueIndex < _currentPlaylist.Tracks.Count && _currentPlaylist.Tracks[queueIndex] == track
                ? queueIndex
                : IndexOfInQueue(track);

            // Moving on into the songs lined up by hand uses up the ones passed;
            // a jump anywhere else - back, or past the end of them - leaves
            // nothing lined up. Starting the same slot again (repeat) is neither.
            if (_linedUpCount > 0 && _queueIndex != previousIndex)
            {
                var step = _queueIndex - previousIndex;
                _linedUpCount = previousIndex >= 0 && step > 0 && step <= _linedUpCount ? _linedUpCount - step : 0;
                OnPropertyChanged(nameof(LinedUpCount));
            }

            // Every start of playback ages out any earlier one still waiting on
            // a stream URL - see StartWhenResolved.
            var generation = ++_playGeneration;

            // Every way a track can start playing arrives here - a double-
            // clicked row, Next/Previous, PlayOrPause's fallback, auto-advance
            // on EndReached, the skip-on-failure handler - so this is the one
            // place a placeholder has to become playable. It used to be done by
            // MainViewModel.PlayResolvingPlaceholder, above this class, and the
            // half of those callers that never went through MainViewModel
            // crashed the decoder instead (see IStreamUrlResolver).
            var pending = ResolveForPlaybackAsync(track);
            if (!pending.IsCompleted)
            {
                StartWhenResolved(pending, generation, immediate, startAt);
                return;
            }

            // IsCompletedSuccessfully, not IsCompleted: reading .Result off a
            // faulted task rethrows, and this runs on the UI thread on the
            // caller's stack - so a resolver that threw instead of returning
            // null (the contract, but not something this class can enforce)
            // turned "activate a track that cannot be played" into a crash.
            // A track that cannot be resolved is simply not played.
            if (!pending.IsCompletedSuccessfully || pending.Result is not { } playable)
            {
                LogUnplayable(track, pending);
                return;
            }

            Start(playable, immediate, startAt);
        }

        // Makes tracks the queue and starts the one at index, part-way through
        // it when startAt says so - an album picked back up from the shelf.
        public void PlayQueueFrom(IReadOnlyList<Track> tracks, int index, TimeSpan startAt)
        {
            if (index < 0 || index >= tracks.Count)
                return;

            SetCurrentPlaylist(new Playlist("Now Playing Queue", new List<Track>(tracks)));
            Play(tracks[index], index, startAt: startAt > TimeSpan.Zero ? startAt : null);
        }

        // Everything after the track is known to be playable. Split out only so
        // the deferred path below can rejoin here rather than restating it.
        private void Start(Track track, bool immediate, TimeSpan? startAt)
        {
            _logger.LogInformation("Playing {Title} by {Artist} ({Path})", track.Title, track.Artists, LogPath.Short(track.Path));

            // Everything owed to the track being left behind, before anything
            // about the new one is set - see _positionTrack on why the order
            // matters.
            LeavePlayingTrack();

            // Before Play, like everything else here about the song being
            // left: a position reported by the new one must not be written
            // down as the old one's.
            AlbumProgress.TrackStarted(track, IsShuffleEnabled);

            _parkedAtQueueTop = false;
            SelectedTrack = track;
            CurrentlyPlayingTrack = track;

            // Both worked out before Play, not after: Play can raise Playing
            // synchronously on some heads, and ApplyPendingSeek reads _pendingSeek
            // from that handler.
            _pendingSeek = startAt ?? ResumeTargetFor(track);
            _positionTrack = track.RememberPlaybackPosition ? track : null;
            _lastKnownTimeMs = 0;
            _listen.Begin(track);

            _audioManager.Play(track, immediate);
            ApplyVolumeAdjustment(track);

            // Arms decode-ahead for whichever track should follow this one,
            // so the gapless pipeline can splice it in with no gap.
            ArmUpcoming(GetUpcomingEntry(track, _queueIndex).Track);

            // Drives the History sidebar view - see Track.LastPlayedAt/
            // Library.RecordPlayed for why this stamps here rather than
            // alongside IncrementPlayCount, which waits until the track has
            // been heard (see ListenMeter). Raises TrackChanged as a
            // PlayStarted - same reasoning as the EndReached handler above.
            _library.RecordPlayed(track);
        }

        // The browser path: the stream URL is a round trip away (a ticket has to
        // be minted for this exact track - see StreamTicketUrlResolver), so the
        // start finishes when it lands instead of on this stack. Every other
        // head resolves synchronously and never reaches here.
        //
        // Back onto the UI thread, because Start touches observable properties
        // the view is bound to. The generation check is what makes a slow
        // resolve safe: pressing Next twice while the first URL is still in
        // flight must not have the first track suddenly take over once it
        // arrives - only the most recent request may still start something.
        private void StartWhenResolved(Task<Track?> pending, int generation, bool immediate, TimeSpan? startAt)
        {
            _ = pending.ContinueWith(resolved => Dispatcher.UIThread.Post(() =>
            {
                if (generation != _playGeneration)
                {
                    _logger.LogDebug("Discarding a stream URL that arrived after another track was started");
                    return;
                }

                if (resolved.IsCompletedSuccessfully && resolved.Result is { } playable)
                    Start(playable, immediate, startAt);
                else
                    LogUnplayable(null, resolved);
            }));
        }

        // Hands the audio pipeline whatever should follow the current track.
        // Deferred exactly like the start above when the URL isn't in hand yet -
        // which is also what primes the browser's ticket cache, so auto-advance
        // onto a streamed track normally finds a URL already minted rather than
        // pausing for one.
        private void ArmUpcoming(Track? upcoming)
        {
            // Decode-ahead starts a track at its beginning and splices it in
            // with no gap, which is the one thing a track that resumes part-way
            // through must not do. Arming nothing means the advance goes through
            // the ordinary Play path on EndReached instead - a small gap, and
            // the right position.
            if (upcoming != null && ResumeTargetFor(upcoming) != null)
                upcoming = null;

            var pending = ResolveForPlaybackAsync(upcoming);
            if (pending.IsCompleted)
            {
                // Same faulted-task care as Play above - arming nothing is the
                // right answer for a track that could not be resolved, and it
                // is what SetUpcoming(null) already means.
                _audioManager.SetUpcoming(pending.IsCompletedSuccessfully ? pending.Result : null);
                return;
            }

            var generation = _playGeneration;
            _ = pending.ContinueWith(resolved => Dispatcher.UIThread.Post(() =>
            {
                // Nothing to arm for a track that is no longer the current one's
                // successor.
                if (generation == _playGeneration)
                    _audioManager.SetUpcoming(resolved.IsCompletedSuccessfully ? resolved.Result : null);
            }));
        }

        // One line for either way a track fails to become playable: the
        // resolver answered "no" (already logged by it, with the reason), or it
        // threw, which is a bug in the resolver worth naming here rather than
        // swallowing silently.
        private void LogUnplayable(Track? track, Task<Track?> resolve)
        {
            if (resolve.Exception is { } ex)
                _logger.LogWarning(ex, "Resolving {Title} for playback threw instead of declining it", track?.Title);
        }

        // A track ready to hand to IAudioManager, or null if it is not playable
        // right now. A local file passes straight through; a placeholder needs a
        // stream URL from the peer that holds it, and gets a transient copy
        // carrying that URL rather than having its own Path mutated - the
        // placeholder lives in Library.Tracks and a stream URL must never be
        // persisted there.
        //
        // Clone() rather than a `with` expression on a record, because Clone
        // keeps Track.Id: the copy is still the same track as far as the play
        // queue is concerned. A differing Path once made the copy compare
        // unequal to the queued placeholder, so IndexOf returned -1 and
        // auto-advance jumped back to the front of the queue.
        //
        // A task because one head cannot answer without a network round trip -
        // see IStreamUrlResolver. Everywhere else the returned task is already
        // completed and playback still starts on the caller's own stack.
        private async Task<Track?> ResolveForPlaybackAsync(Track? track)
        {
            if (track == null || track.Path != null)
                return track;

            string? streamUrl = null;
            if (_streamUrlResolver != null)
                streamUrl = await _streamUrlResolver.ResolveAsync(track);

            if (streamUrl == null)
            {
                // Already logged by the resolver, with the actual reason.
                _logger.LogWarning("Not playing {Title}: it is not downloaded and no stream URL could be built", track.Title);
                return null;
            }

            var streaming = track.Clone();
            streaming.Path = streamUrl;
            return streaming;
        }

        // Set while the queue has played out and been parked back at its first
        // track: something is "currently playing" as far as the rest of the app
        // and the OS now-playing card are concerned, but nothing is loaded
        // behind it, so play has to start that track rather than resume.
        private bool _parkedAtQueueTop;

        // The album finished. Rather than clearing the current track - which is
        // what a stop does, and which takes the Lock Screen/Control Center card
        // down with it - the queue is wound back to its first track and left
        // there, paused at the beginning. The card stays up showing that track,
        // so the album can be started again from it without opening the app,
        // and pressing play anywhere does exactly what it says.
        //
        // Only ever reached with nothing playing: the audio manager raises
        // PlayedOut after it has put its own output down. See
        // GaplessAudioManager.CheckWhetherPlayedOut.
        private void ParkAtTopOfQueue()
        {
            // The finished track's own bookkeeping - its resume position (the
            // EndReached handler has already cleared it, but a queue that ran
            // out after a run of failures has not) and its volume adjustment,
            // which must not be left applied to whatever plays next.
            LeavePlayingTrack();

            var first = _currentPlaylist.Tracks.FirstOrDefault();
            _logger.LogInformation(
                "Queue played out; parking at {Title}", first?.Title ?? "nothing - the queue is empty");

            _queueIndex = first != null ? 0 : -1;
            _parkedAtQueueTop = first != null;
            if (first != null)
                SelectedTrack = first;
            CurrentlyPlayingTrack = first;
            OnPropertyChanged(nameof(IsPlaying));
        }

        public void PlayOrPause(Track track)
        {
            if (_audioManager.IsPlaying)
            {
                _audioManager.Pause();
            }
            else if (_parkedAtQueueTop && CurrentlyPlayingTrack is { } parked)
            {
                // Parked at the top of a queue that finished: there is nothing
                // to resume - the coordinator let its decoder go when the album
                // ended - so this starts the album again from the top, which is
                // what the card showing that first track promises.
                Play(parked, _queueIndex);
            }
            else
            {
                if (CanResume)
                {
                    _audioManager.Resume();
                }
                else
                {
                    Play(track);
                }
            }
        }

        // ── Per-track playback options ─────────────────────────────────────
        //
        // Three options that live on the Track and only mean anything at the
        // moment it starts or stops playing: resume where it left off, stay out
        // of shuffle's way, and play at its own volume. See Track's section of
        // the same name for what each one is for; this is where they take
        // effect.

        // Which slots shuffle is allowed to land on: everything except the
        // current one and anything marked IgnoreWhenShuffling.
        private static List<int> ShuffleCandidates(IReadOnlyList<Track> tracks, int currentIndex)
        {
            var candidates = new List<int>(tracks.Count);
            for (var i = 0; i < tracks.Count; i++)
            {
                if (i != currentIndex && !tracks[i].IgnoreWhenShuffling)
                    candidates.Add(i);
            }

            return candidates;
        }

        // Where this track should start, or null for "at the beginning" - which
        // covers the option being off, nothing recorded yet, and the two edge
        // cases worth naming: a position in the first few seconds is not worth
        // restoring, and one within a few seconds of the end would restart the
        // track only to have it end immediately.
        private static readonly TimeSpan ResumeThreshold = TimeSpan.FromSeconds(5);

        private static TimeSpan? ResumeTargetFor(Track track)
        {
            if (!track.RememberPlaybackPosition || track.ResumePosition is not { } position)
                return null;

            if (position < ResumeThreshold)
                return null;

            if (track.Duration > TimeSpan.Zero && position > track.Duration - ResumeThreshold)
                return null;

            return position;
        }

        private void ApplyPendingSeek()
        {
            if (_pendingSeek is not { } target)
                return;

            _pendingSeek = null;

            // Length is what the decoder actually reports for the file being
            // played; Duration is what the tag said when it was scanned. They
            // normally agree, and where they don't the decoder is the one that
            // Position is a fraction of.
            var lengthMs = _audioManager.Length > 0
                ? _audioManager.Length
                : (long)((_positionTrack ?? CurrentlyPlayingTrack)?.Duration.TotalMilliseconds ?? 0);

            if (lengthMs <= 0)
                return;

            _audioManager.Position = (float)Math.Clamp(target.TotalMilliseconds / lengthMs, 0d, 1d);
            _listen.ResumedAt(target);
            _logger.LogDebug("Resuming at {Position}", target);
        }

        // Called whenever playback of the current track ends for any reason -
        // a new track starting, a stop, the queue emptying. Saves where it got
        // to and hands the volume back.
        private void LeavePlayingTrack()
        {
            SaveResumePosition();
            _positionTrack = null;
            RestoreVolume();
        }

        // The app is going away (quit on desktop, backgrounded on a phone) while
        // something may still be playing. Every other way of leaving a track
        // goes through a pause/stop/track-change event, and quitting goes
        // through none of them - so without this, listening to a podcast and
        // then quitting is precisely the case that loses the position, which is
        // the case the option exists for. Synchronous: Library writes the one
        // row on the spot, and there is no later to defer to.
        //
        // Wired in App.axaml.cs - the desktop window's Closing, and the
        // activatable lifetime's Deactivated on mobile.
        public void SavePlaybackState()
        {
            SaveResumePosition();
            AlbumProgress.Flush(synchronously: true);
        }

        private void SaveResumePosition()
        {
            if (_positionTrack is not { } track || _lastKnownTimeMs <= 0)
                return;

            _library.RecordResumePosition(track, TimeSpan.FromMilliseconds(_lastKnownTimeMs));
        }

        // The track's own adjustment, handed to the audio manager as an offset
        // on top of whatever the user's volume currently is (see
        // IAudioManager.VolumeOffset). Nothing here touches Volume itself, so
        // the slider does not move, and a drag mid-track sets the volume this
        // offset then applies against rather than being fought over.
        private void ApplyVolumeAdjustment(Track track)
        {
            _audioManager.VolumeOffset = track.VolumeAdjustment;
            if (track.VolumeAdjustment != 0)
            {
                _logger.LogDebug(
                    "Per-track volume adjustment {Adjustment:+#;-#;0} for {Title}",
                    track.VolumeAdjustment, track.Title);
            }
        }

        private void RestoreVolume() => _audioManager.VolumeOffset = 0;

        public void Next()
        {
            if (CurrentlyPlayingTrack != null)
            {
                var next = GetNextEntry(CurrentlyPlayingTrack, ResolveQueueIndex(CurrentlyPlayingTrack));
                if (next.Track != null)
                {
                    Play(next.Track, next.Index);
                }
            }
        }

        // How far into a track Previous() rewinds it instead of stepping back a
        // track - the CD player convention: a press a few seconds in restarts
        // the song, and a second press, now at the start, goes to the one
        // before it.
        private static readonly TimeSpan PreviousRewindThreshold = TimeSpan.FromSeconds(3);

        public void Previous()
        {
            if (CurrentlyPlayingTrack != null)
            {
                if (_audioManager.Time > PreviousRewindThreshold.TotalMilliseconds)
                {
                    _audioManager.Position = 0;
                    return;
                }

                var previous = GetPreviousEntry(ResolveQueueIndex(CurrentlyPlayingTrack));
                if (previous.Track != null)
                {
                    Play(previous.Track, previous.Index);
                }
            }
        }
    }
}
