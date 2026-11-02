using System;
using System.Collections.Generic;
using System.IO;

using Flower.SemanticAudio;

namespace Flower.Tests.SemanticAudio;

// Songs that exist only as a length. Window k of every song reads as a
// constant whose value names it (WindowValue), so FakeClapAudioEncoder can
// tell which window it was given.
public sealed class FakeClipSource : IClipSource
{
    private readonly Dictionary<string, TimeSpan> _songs = new();
    private readonly HashSet<string> _unreadable = new();

    public List<string> Opens { get; } = new();

    public bool IsAvailable { get; set; } = true;

    public List<(string Path, int Window)> Reads { get; } = new();

    public void Add(string path, TimeSpan duration) => _songs[path] = duration;

    public void MakeUnreadable(string path) => _unreadable.Add(path);

    public static float WindowValue(int window) => (window + 1) / 1000f;

    public IClipReader Open(string path)
    {
        Opens.Add(path);
        if (_unreadable.Contains(path) || !_songs.TryGetValue(path, out var duration))
            throw new IOException($"{path} is not a song");

        return new Reader(this, path, duration);
    }

    private sealed class Reader(FakeClipSource source, string path, TimeSpan duration) : IClipReader
    {
        public TimeSpan? Duration => duration;

        public float[] ReadWindow(int window)
        {
            source.Reads.Add((path, window));
            var start = (long)window * Clap.ClipSamples;
            var length = Math.Clamp(ClipGrid.SampleCount(duration) - start, 0, Clap.ClipSamples);
            var samples = new float[length];
            Array.Fill(samples, WindowValue(window));
            return samples;
        }

        public void Dispose()
        {
        }
    }
}
