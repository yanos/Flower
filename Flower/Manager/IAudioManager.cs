using System;

using Flower.Models;

namespace Flower.Manager
{
    public interface IAudioManager
    {
        bool IsPlaying { get; }
        //bool CanResume { get; }

        int Volume { get; set; }
        float Position { get; set; }

        void Play(Track track);
        void Resume();
        void Pause();
        void Stop();

        public event EventHandler? Paused;
        public event EventHandler? Stopped;
        public event EventHandler? Playing;
        public event EventHandler? PositionChanged;
        public event EventHandler? VolumeChanged;
        public event EventHandler? EndReached;
    }
}