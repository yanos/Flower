using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;

using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Manager;
using Flower.Services;
using Flower.Models;

namespace Flower.ViewModels
{
    public class CurrentlyPlayingControlViewModel : ViewModelBase, IDisposable
    {
        private readonly PlaylistControlViewModel _playlistControlViewModel;
        private readonly IAudioManager _audioManager;
        private readonly Library _library;
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
        public string Subtitle => CurrentlyPlayingTrack is { } track
            ? $"{track.Artists} — {track.Album} ({track.Year})"
            : " ";

        public Bitmap? AlbumArt
        {
            get => _albumArt;
            private set { _albumArt?.Dispose(); _albumArt = value; OnPropertyChanged(); }
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

        private static readonly string[] _imageExtensions =
            [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif"];

        private void LoadAlbumArt(Track? track)
        {
            // A live peer-stream URL (see PeerLibraryViewModel.ToTransientTrack)
            // is not a local filesystem path - skip straight to no-art instead of
            // throwing TagLib/IO exceptions trying to read it as one.
            // TrackDecoder.EnsureMedia uses the same "://" check to tell them apart.
            if (track?.Path is not { } path || path.Contains("://")) { AlbumArt = null; return; }

            _ = Task.Run(() =>
            {
                Bitmap? bitmap = null;

                // 1. Embedded tag art
                try
                {
                    using var tagFile = TagLib.File.Create(track.Path);
                    var pic = tagFile.Tag.Pictures.FirstOrDefault();
                    if (pic?.Data?.Data is { Length: > 0 } data)
                    {
                        using var ms = new MemoryStream(data);
                        bitmap = new Bitmap(ms);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not read/decode embedded art for {Path}", track.Path);
                }

                // 2. cover.* / folder.* in the same directory
                if (bitmap == null)
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(track.Path);
                        if (dir != null)
                        {
                            var file = Directory.EnumerateFiles(dir)
                                .FirstOrDefault(f =>
                                {
                                    var stem = Path.GetFileNameWithoutExtension(f);
                                    var ext  = Path.GetExtension(f).ToLowerInvariant();
                                    return (stem.Equals("cover",  StringComparison.OrdinalIgnoreCase) ||
                                            stem.Equals("folder", StringComparison.OrdinalIgnoreCase))
                                        && _imageExtensions.Contains(ext);
                                });
                            if (file != null)
                                bitmap = new Bitmap(file);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not read/decode a cover/folder image next to {Path}", track.Path);
                    }
                }

                // 3. Embedded art from another track on the same album
                if (bitmap == null && !string.IsNullOrEmpty(track.Album))
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
                            _logger.LogDebug(ex, "Could not read/decode embedded art from album sibling {Path}", sibling.Path);
                        }
                    }
                }

                Dispatcher.UIThread.Post(() => AlbumArt = bitmap);
            });
        }

        public CurrentlyPlayingControlViewModel(
            PlaylistControlViewModel playlistControlViewModel,
            IAudioManager audioManager,
            Library library,
            ILogger<CurrentlyPlayingControlViewModel> logger)
        {
            _playlistControlViewModel = playlistControlViewModel;
            _audioManager = audioManager;
            _library = library;
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
