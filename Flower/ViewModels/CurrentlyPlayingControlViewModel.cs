using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Audio;
using Flower.Logging;
using Flower.Services;
using Flower.Models;

namespace Flower.ViewModels
{
    public class CurrentlyPlayingControlViewModel : ViewModelBase, IDisposable
    {
        private readonly PlaylistControlViewModel _playlistControlViewModel;
        private readonly IAudioManager _audioManager;
        private readonly Library _library;
        private readonly AlbumArtLoader _albumArtLoader;
        private readonly ILogger<CurrentlyPlayingControlViewModel> _logger;

        private double _seekPosition;
        private bool _isUpdatingFromAudio;
        private Bitmap? _albumArt;

        // Debounces the seek bar: a drag fires SeekPosition's setter on
        // every pointer-move tick, and each one used to issue an immediate
        // native LibVLC seek. Firing those back-to-back, faster than a seek
        // can settle, was confirmed (via GaplessCoordinator/TrackDecoder
        // logging during a real repro) to wedge the decode pipeline
        // permanently - the shared ring's writer just stops producing PCM
        // forever, and the render sink starts repeating its last buffer.
        // Only the last position after a short pause in dragging is ever
        // actually sent to the audio manager.
        private readonly Timer _seekDebounceTimer;
        private float _pendingSeekPosition;

        public Track? CurrentlyPlayingTrack => _playlistControlViewModel.CurrentlyPlayingTrack;

        public bool IsRepeatEnabled => _playlistControlViewModel.IsRepeatEnabled;

        public bool IsShuffleEnabled => _playlistControlViewModel.IsShuffleEnabled;

        // Always rendered (never IsVisible=false) so the control's height stays constant
        // whether or not a track is playing, instead of growing when playback starts.
        //
        // The year is parenthesised only when there is one: an untagged file used
        // to render a bare "()" after the album. Possible on any head, but caught
        // in the browser, where every row comes from a server whose own import
        // may not have had a year to give.
        public string Subtitle => CurrentlyPlayingTrack is { } track
            ? string.IsNullOrWhiteSpace(track.Year)
                ? $"{track.Artists} — {track.Album}"
                : $"{track.Artists} — {track.Album} ({track.Year})"
            : " ";

        // Not disposed on replacement, and deliberately so. These bitmaps come
        // from AlbumArtLoader, which caches them per album and hands the same
        // instance to every track row showing that album - disposing one here
        // would blank the list. The loader owns their lifetime (weak-referenced,
        // so the GC can still reclaim them); this only holds a reference.
        // Before the shared loader, each was a private decode this class had
        // made itself, and disposing was correct.
        public Bitmap? AlbumArt
        {
            get => _albumArt;
            private set { _albumArt = value; OnPropertyChanged(); }
        }

        public double SeekPosition
        {
            get => _seekPosition;
            set
            {
                _seekPosition = value;
                OnPropertyChanged();
                if (_isUpdatingFromAudio || !_audioManager.IsPlaying)
                    return;

                _pendingSeekPosition = (float)value;
                _seekDebounceTimer.Stop();
                _seekDebounceTimer.Start();
            }
        }

        public string? ElapsedTime => _audioManager.Time > 0
            ? FormatDuration(TimeSpan.FromMilliseconds(_audioManager.Time))
            : null;

        public string? TotalTime
        {
            get
            {
                var track = CurrentlyPlayingTrack;
                TimeSpan ts;
                if (track != null && track.Duration > TimeSpan.Zero)
                    ts = track.Duration;
                else if (_audioManager.Length > 0)
                    ts = TimeSpan.FromMilliseconds(_audioManager.Length);
                else
                    return null;
                return FormatDuration(ts);
            }
        }

        public void ToggleRepeat()
        {
            _playlistControlViewModel.ToggleRepeat();
        }

        public void ToggleShuffle()
        {
            _playlistControlViewModel.ToggleShuffle();
        }

        private static string FormatDuration(TimeSpan ts)
            => (int)ts.TotalHours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

        // Which track's art is wanted now. A remote fetch is a network round
        // trip (see AlbumArtLoader.LoadRemoteAsync), so pressing Next twice in
        // quick succession can land the first track's art after the second's -
        // the same out-of-order hazard PlaylistControlViewModel guards with its
        // own _playGeneration. Reading a local file was fast enough that this
        // never showed up before there was a browser head.
        private int _artGeneration;

        private void LoadAlbumArt(Track? track)
        {
            var generation = ++_artGeneration;

            if (track == null)
            {
                AlbumArt = null;
                return;
            }

            _ = Task.Run(async () =>
            {
                // The one implementation of "what is this track's art", shared
                // with the track list and TrackInfoWindow, and the only one that
                // knows a placeholder's art lives on the origin server. This
                // used to be a second, filesystem-only copy of the embedded-tag
                // and cover/folder lookup - which is why a browser tab showed
                // covers on every row and a blank square in the top bar.
                var bitmap = await _albumArtLoader.LoadAsync(track);

                // Art from another track on the same album, which the shared
                // loader deliberately does not do. It caches per album and only
                // retains hits, so a miss would re-walk the library on every
                // row that scrolled past - affordable once per track change
                // here, not once per row there.
                if (bitmap == null && AlbumArtLoader.IsLocalFile(track) && !string.IsNullOrEmpty(track.Album))
                {
                    // t.Path != null - a sibling can be a sync placeholder
                    // (no local file yet, see SYNC-PLAN.md's library sync)
                    // that TagLib.File.Create would otherwise throw on.
                    var siblings = _library.Tracks
                        .Where(t => t.Path != null && t.Path != track.Path &&
                                    string.Equals(t.Album, track.Album, StringComparison.OrdinalIgnoreCase));
                    foreach (var sibling in siblings)
                    {
                        try
                        {
                            using var tagFile = TagLib.File.Create(sibling.Path);
                            var pic = tagFile.Tag.Pictures.FirstOrDefault();
                            if (pic?.Data?.Data is { Length: > 0 } data)
                            {
                                using var ms = new MemoryStream(data);
                                bitmap = new Bitmap(ms);
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Could not read/decode embedded art from album sibling {Path}", LogPath.Short(sibling.Path));
                        }
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == _artGeneration)
                        AlbumArt = bitmap;
                });
            });
        }

        public CurrentlyPlayingControlViewModel(
            PlaylistControlViewModel playlistControlViewModel,
            IAudioManager audioManager,
            Library library,
            AlbumArtLoader albumArtLoader,
            ILogger<CurrentlyPlayingControlViewModel> logger)
        {
            _playlistControlViewModel = playlistControlViewModel;
            _audioManager = audioManager;
            _library = library;
            _albumArtLoader = albumArtLoader;
            _logger = logger;

            _seekDebounceTimer = new Timer(150) { AutoReset = false };
            _seekDebounceTimer.Elapsed += (_, _) => _audioManager.Position = _pendingSeekPosition;

            _subscriptions.Add<System.ComponentModel.PropertyChangedEventHandler>((s, e) =>
            {
                if (e.PropertyName == nameof(_playlistControlViewModel.CurrentlyPlayingTrack))
                {
                    OnPropertyChanged(nameof(CurrentlyPlayingTrack));
                    OnPropertyChanged(nameof(Subtitle));
                    OnPropertyChanged(nameof(TotalTime));
                    LoadAlbumArt(_playlistControlViewModel.CurrentlyPlayingTrack);
                }
                else if (e.PropertyName == nameof(_playlistControlViewModel.IsRepeatEnabled))
                {
                    OnPropertyChanged(nameof(IsRepeatEnabled));
                }
                else if (e.PropertyName == nameof(_playlistControlViewModel.IsShuffleEnabled))
                {
                    OnPropertyChanged(nameof(IsShuffleEnabled));
                }
            },
                h => _playlistControlViewModel.PropertyChanged += h, h => _playlistControlViewModel.PropertyChanged -= h);

            _subscriptions.Add<EventHandler>((s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _isUpdatingFromAudio = true;
                    SeekPosition = _audioManager.Position;
                    _isUpdatingFromAudio = false;
                    OnPropertyChanged(nameof(ElapsedTime));
                    OnPropertyChanged(nameof(TotalTime));
                });
            },
                h => _audioManager.PositionChanged += h, h => _audioManager.PositionChanged -= h);

            _subscriptions.Add<EventHandler>((s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _isUpdatingFromAudio = true;
                    SeekPosition = 0;
                    _isUpdatingFromAudio = false;
                    OnPropertyChanged(nameof(ElapsedTime));
                    OnPropertyChanged(nameof(TotalTime));
                });
            },
                h => _audioManager.Stopped += h, h => _audioManager.Stopped -= h);
        }

        // Every event this class attaches to in its constructor, paired with
        // its teardown - see SubscriptionBag, and docs/ARCHITECTURE-REVIEW.md
        // Tier 2.3. The seek-debounce Timer is owned outright rather than
        // subscribed to, so it is disposed rather than unsubscribed.
        private readonly SubscriptionBag _subscriptions = new();

        public void Dispose()
        {
            _subscriptions.Dispose();
            _seekDebounceTimer.Dispose();
        }
    }
}
