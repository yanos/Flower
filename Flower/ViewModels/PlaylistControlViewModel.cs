using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Manager;
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
            IStreamUrlResolver? streamUrlResolver = null)
        {
            _streamUrlResolver = streamUrlResolver;
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
                OnPropertyChanged(nameof(IsPlaying));
            },
                h => _audioManager.Playing += h, h => _audioManager.Playing -= h);

            _subscriptions.Add<EventHandler>((s, e) =>
            {
                OnPropertyChanged(nameof(IsPlaying));
                CurrentlyPlayingTrack = null;
            },
                h => _audioManager.Stopped += h, h => _audioManager.Stopped -= h);

            _subscriptions.Add<EventHandler>((s, e) =>
            {
                OnPropertyChanged(nameof(IsPlaying));
            },
                h => _audioManager.Paused += h, h => _audioManager.Paused -= h);

            // Synchronous: the play-count write is one indexed UPDATE issued
            // by Library itself (see its ITrackStore). This used to be async
            // void over an await on LibraryStore.SaveAsync, on a LibVLC
            // callback thread.
            _subscriptions.Add<EventHandler>((s, e) =>
            {
                if (CurrentlyPlayingTrack != null)
                {
                    var finishedTrack = CurrentlyPlayingTrack;
                    _logger.LogDebug("EndReached: {Title} ({Path})", finishedTrack.Title, finishedTrack.Path);

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
                    // IncrementPlayCount raises Library.TrackStatsChanged and
                    // persists the new count itself. Deliberately *not* NotifyTrackChanged: the track
                    // list hasn't changed, only one track's counter, and
                    // TracksUpdated means a full UI rebuild plus a peer library
                    // sync - twice per song. See ARCHITECTURE-REVIEW Tier 1.1.
                    _library.IncrementPlayCount(finishedTrack);

                    var next = GetUpcomingEntry(finishedTrack, ResolveQueueIndex(finishedTrack));
                    if (next.Track != null)
                    {
                        _logger.LogDebug("Auto-advancing to {Title} (repeat={Repeat}, shuffle={Shuffle})", next.Track.Title, IsRepeatEnabled, IsShuffleEnabled);
                        Dispatcher.UIThread.Post(() => Play(next.Track, next.Index));
                    }
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
                _logger.LogWarning("Skipping {Title} ({Path}) - it could not be played", e.Track.Title, e.Track.Path);
                PlaybackFailed?.Invoke(this, e);

                // Repeat would re-attempt the same broken file forever, so the
                // next track here is always the *next* one, never a repeat of
                // this one.
                var next = GetNextEntry(e.Track, ResolveQueueIndex(e.Track));
                if (next.Track != null && next.Track != e.Track)
                    Dispatcher.UIThread.Post(() => Play(next.Track, next.Index));
            },
                h => _audioManager.TrackFailed += h, h => _audioManager.TrackFailed -= h);
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
        private (Track? Track, int Index) GetUpcomingEntry(Track currentTrack, int currentIndex) =>
            IsRepeatEnabled ? (currentTrack, currentIndex) : GetNextEntry(currentTrack, currentIndex);

        private (Track? Track, int Index) GetNextEntry(Track currentTrack, int currentIndex)
        {
            var tracks = _currentPlaylist.Tracks;
            if (tracks.Count == 0)
                return (null, -1);

            if (IsShuffleEnabled && tracks.Count > 1)
            {
                // Re-rolls on the current *slot*, not the current track: with
                // duplicates in the queue, excluding by value would refuse to
                // shuffle into the other copy, and a queue of nothing but
                // copies of one track would spin here forever.
                int index;
                do
                {
                    index = _random.Next(tracks.Count);
                } while (index == currentIndex);
                return (tracks[index], index);
            }

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
        public void Play(Track track, int queueIndex)
        {
            // Worked out against the track as queued - the placeholder - since
            // that is what the queue holds. ResolveForPlayback's copy keeps
            // Track.Id, so this stays correct either way, but doing it first
            // makes that independent of the copy's behaviour.
            _queueIndex = queueIndex >= 0 && queueIndex < _currentPlaylist.Tracks.Count && _currentPlaylist.Tracks[queueIndex] == track
                ? queueIndex
                : IndexOfInQueue(track);

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
                StartWhenResolved(pending, generation);
                return;
            }

            if (pending.Result is not { } playable)
                return;

            Start(playable);
        }

        // Everything after the track is known to be playable. Split out only so
        // the deferred path below can rejoin here rather than restating it.
        private void Start(Track track)
        {
            _logger.LogInformation("Playing {Title} by {Artist} ({Path})", track.Title, track.Artists, track.Path);
            SelectedTrack = track;
            CurrentlyPlayingTrack = track;
            _audioManager.Play(track);

            // Arms decode-ahead for whichever track should follow this one,
            // so the gapless pipeline can splice it in with no gap.
            ArmUpcoming(GetUpcomingEntry(track, _queueIndex).Track);

            // Drives the History sidebar view - see Track.LastPlayedAt/
            // Library.RecordPlayed for why this stamps here rather than
            // alongside IncrementPlayCount in the EndReached handler below.
            // Raises TrackStatsChanged, not TracksUpdated - same reasoning as
            // the EndReached handler above.
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
        private void StartWhenResolved(Task<Track?> pending, int generation)
        {
            _ = pending.ContinueWith(resolved => Dispatcher.UIThread.Post(() =>
            {
                if (generation != _playGeneration)
                {
                    _logger.LogDebug("Discarding a stream URL that arrived after another track was started");
                    return;
                }

                if (resolved.Result is { } playable)
                    Start(playable);
            }));
        }

        // Hands the audio pipeline whatever should follow the current track.
        // Deferred exactly like the start above when the URL isn't in hand yet -
        // which is also what primes the browser's ticket cache, so auto-advance
        // onto a streamed track normally finds a URL already minted rather than
        // pausing for one.
        private void ArmUpcoming(Track? upcoming)
        {
            var pending = ResolveForPlaybackAsync(upcoming);
            if (pending.IsCompleted)
            {
                _audioManager.SetUpcoming(pending.Result);
                return;
            }

            var generation = _playGeneration;
            _ = pending.ContinueWith(resolved => Dispatcher.UIThread.Post(() =>
            {
                // Nothing to arm for a track that is no longer the current one's
                // successor.
                if (generation == _playGeneration)
                    _audioManager.SetUpcoming(resolved.Result);
            }));
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

        public void PlayOrPause(Track track)
        {
            if (_audioManager.IsPlaying)
            {
                _audioManager.Pause();
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

        public void Previous()
        {
            if (CurrentlyPlayingTrack != null)
            {
                var previous = GetPreviousEntry(ResolveQueueIndex(CurrentlyPlayingTrack));
                if (previous.Track != null)
                {
                    Play(previous.Track, previous.Index);
                }
            }
        }
    }
}
