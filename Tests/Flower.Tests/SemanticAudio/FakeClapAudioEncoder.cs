using System;
using System.Collections.Generic;

using Flower.SemanticAudio;

namespace Flower.Tests.SemanticAudio;

// Reads which window it was given back out of FakeClipSource's samples and
// counts it; the embedding is that window's axis, so every clip is distinct.
public sealed class FakeClapAudioEncoder : IClapAudioEncoder
{
    public List<int> Encoded { get; } = new();

    public float[] Encode(float[] waveform)
    {
        var window = (int)Math.Round(waveform[0] * 1000) - 1;
        Encoded.Add(window);
        var vector = new float[Clap.Dimensions];
        vector[window % Clap.Dimensions] = 1;
        return vector;
    }

    public void Dispose()
    {
    }
}
