using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Flower.Manager;
using Flower.Models;

namespace Flower.ViewModels
{
    public class CurrentlyPlayingControlViewModel : ViewModelBase
    {
        private readonly PlaylistControlViewModel _playlistControlViewModel;
        private readonly IAudioManager _audioManager;

        private double _seekPosition;
        private bool _isUpdatingFromAudio;
        private Bitmap? _albumArt;

        public Track? CurrentlyPlayingTrack => _playlistControlViewModel.CurrentlyPlayingTrack;

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
                if (!_isUpdatingFromAudio && _audioManager.IsPlaying)
                    _audioManager.Position = (float)value;
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

        private static string FormatDuration(TimeSpan ts)
            => (int)ts.TotalHours > 0 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");

        private static readonly string[] _imageExtensions =
            [".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tiff", ".tif"];

        private void LoadAlbumArt(Track? track)
        {
            if (track?.Path == null) { AlbumArt = null; return; }

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
                catch { }

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
                    catch { }
                }

                Dispatcher.UIThread.Post(() => AlbumArt = bitmap);
            });
        }

        public CurrentlyPlayingControlViewModel(
            PlaylistControlViewModel playlistControlViewModel,
            IAudioManager audioManager)
        {
            _playlistControlViewModel = playlistControlViewModel;
            _audioManager = audioManager;

            _playlistControlViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_playlistControlViewModel.CurrentlyPlayingTrack))
                {
                    OnPropertyChanged(nameof(CurrentlyPlayingTrack));
                    OnPropertyChanged(nameof(TotalTime));
                    LoadAlbumArt(_playlistControlViewModel.CurrentlyPlayingTrack);
                }
            };

            _audioManager.PositionChanged += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _isUpdatingFromAudio = true;
                    SeekPosition = _audioManager.Position;
                    _isUpdatingFromAudio = false;
                    OnPropertyChanged(nameof(ElapsedTime));
                    OnPropertyChanged(nameof(TotalTime));
                });
            };

            _audioManager.Stopped += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _isUpdatingFromAudio = true;
                    SeekPosition = 0;
                    _isUpdatingFromAudio = false;
                    OnPropertyChanged(nameof(ElapsedTime));
                    OnPropertyChanged(nameof(TotalTime));
                });
            };
        }
    }
}
