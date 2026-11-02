using System.Collections.Generic;
using System.Linq;

using Flower.SemanticAudio;

namespace Flower.Tests.SemanticAudio;

// Texts to vectors the test chose. The filter phrases go to four axes of
// their own, so a clip's vocal and speech scores are set by how much of
// those axes it holds; any other text is the zero vector.
public sealed class FakeClapTextEncoder : IClapTextEncoder
{
    public const int VocalAxis = 500;
    public const int InstrumentalAxis = 501;
    public const int SpeechAxis = 502;
    public const int MusicAxis = 503;

    private readonly Dictionary<string, float[]> _vectors = new();

    public FakeClapTextEncoder()
    {
        foreach (var phrase in SemanticAudioFilters.VocalPhrases)
            _vectors[phrase] = Axis(VocalAxis);
        foreach (var phrase in SemanticAudioFilters.InstrumentalPhrases)
            _vectors[phrase] = Axis(InstrumentalAxis);
        foreach (var phrase in SemanticAudioFilters.SpeechPhrases)
            _vectors[phrase] = Axis(SpeechAxis);

        // "This audio is instrumental music." is a music phrase as well as an
        // instrumental one; the last word goes to the music axis here, which
        // only matters to the speech score.
        foreach (var phrase in SemanticAudioFilters.MusicPhrases.Except(SemanticAudioFilters.InstrumentalPhrases))
            _vectors[phrase] = Axis(MusicAxis);
    }

    public List<string> Encoded { get; } = new();

    // What a prompt, as typed, encodes to once templated.
    public void SetPrompt(string text, float[] vector) => _vectors[string.Format(Clap.PromptTemplate, text)] = vector;

    public float[][] Encode(IReadOnlyList<string> texts)
    {
        Encoded.AddRange(texts);
        return texts.Select(t => _vectors.TryGetValue(t, out var v) ? v : new float[Clap.Dimensions]).ToArray();
    }

    public static float[] Axis(int axis)
    {
        var vector = new float[Clap.Dimensions];
        vector[axis] = 1;
        return vector;
    }

    // A unit vector from (axis, weight) pairs.
    public static float[] Mix(params (int Axis, float Weight)[] parts)
    {
        var vector = new float[Clap.Dimensions];
        foreach (var (axis, weight) in parts)
            vector[axis] += weight;
        Embedding.Normalize(vector);
        return vector;
    }

    public void Dispose()
    {
    }
}
