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
        long Time { get; }
        long Length { get; }

        void Play(Track track);

        // Tells the manager what should play after the current track, so a
        // gapless implementation can decode it ahead of time. Called by
        // PlaylistControlViewModel right after Play() and after
        // ToggleRepeat()/ToggleShuffle() change what "next" would resolve
        // to. null means nothing should follow (e.g. end of playlist).
        void SetUpcoming(Track? next);

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