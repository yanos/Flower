using System;
using System.Collections.Generic;

namespace Flower.SemanticAudio;

// The two halves of CLAP, each behind the seam the analyzer and the search
// are tested through (fakes return made-up vectors). Both return unit vectors.
public interface IClapAudioEncoder : IDisposable
{
    // One prepared clip (ClipWaveform.Prepare) to its embedding.
    float[] Encode(float[] waveform);
}

public interface IClapTextEncoder : IDisposable
{
    // Texts as given: the prompt template is the search's business, and the
    // filter phrases have their own wording.
    float[][] Encode(IReadOnlyList<string> texts);
}
