using System;
using Flower.Audio;
using Flower.Models;

namespace Flower.Tests.Audio;

public class WebAudioManagerTests
{
    private sealed class Element : IWebAudioElement
    {
        public double Gain;
        public void SetSrc(string url) => Gain = 1;
        public void SetVolume(double volume) => Gain = volume;
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Seek(double seconds) { }
        public double GetCurrentTime() => 0;
        public double GetDuration() => 0;
        public bool GetPaused() => false;
        public bool GetEnded() => false;
    }

    [Fact]
    public void Consecutive_tracks_keep_the_adjustment_and_use_the_native_volume_curve()
    {
        var element = new Element();
        using var audio = new WebAudioManager(element, poll: false);
        audio.Volume = 40;
        audio.VolumeOffset = 10;
        audio.Play(new Track { Path = "first.mp3" });
        Assert.Equal(0.125, element.Gain, 6);
        audio.VolumeOffset = 10;
        audio.Play(new Track { Path = "second.mp3" });
        Assert.Equal(0.125, element.Gain, 6);
        audio.Volume = 70;
        Assert.Equal(Math.Pow(0.8, 3), element.Gain, 6);
        audio.VolumeOffset = -100;
        Assert.Equal(0, element.Gain);
        audio.VolumeOffset = 100;
        Assert.Equal(1, element.Gain);
    }
}
