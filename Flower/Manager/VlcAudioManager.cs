using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Avalonia.Controls.Primitives;
using Avalonia.Threading;

using Flower.Models;

using LibVLCSharp.Shared;

using Track = Flower.Models.Track;

namespace Flower.Manager
{
    public class VlcAudioManager : IAudioManager
    {
        private readonly LibVLC _libVLC;
        private readonly MediaPlayer _mediaPlayer;

        public bool IsPlaying => _mediaPlayer.IsPlaying;

        public int Volume
        {
            get => _mediaPlayer.Volume;
            set => _mediaPlayer.Volume = value;
        }

        public float Position
        {
            get => _mediaPlayer.Position;
            set => _mediaPlayer.Position = value;
        }

        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;

        public VlcAudioManager() 
        {
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);

            _mediaPlayer.Paused += (sender, e) => Paused?.Invoke(this, e);
            _mediaPlayer.Stopped += (sender, e) => Stopped?.Invoke(this, e);
            _mediaPlayer.Playing += (sender, e) => Playing?.Invoke(this, e);
            _mediaPlayer.PositionChanged += (sender, e) => PositionChanged?.Invoke(this, e);
            _mediaPlayer.VolumeChanged += (sender, e) => VolumeChanged?.Invoke(this, e);
            _mediaPlayer.EndReached += (sender, e) => EndReached?.Invoke(this, e);
        }
                
        public void Play(Track track)
        {
            _mediaPlayer.Play(new Media(_libVLC, track.Path));
        }

        public void Resume()
        {
            _mediaPlayer.SetPause(false);
        }

        public void Pause()
        {
            _mediaPlayer.SetPause(true);
        }

        public void Stop()
        {
            _mediaPlayer.Stop();
        }
    }
}
