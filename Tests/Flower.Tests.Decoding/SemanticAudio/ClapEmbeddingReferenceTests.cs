using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

using Flower.SemanticAudio;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.Decoding.SemanticAudio;

// The whole C# pipeline against Python's onnxruntime at the same precision
// (docs/VIBE-PLAYLIST-PLAN.md, "Testing"): a fixture decoded, cut, prepared
// and encoded here must come out at cosine 0.999 or better with what
// make-references.py got, and a prompt tokenized and encoded here likewise.
//
// Needs the model, which is never committed: FLOWER_CLAP_MODEL_DIR names a
// folder holding the release's files (export-clap-onnx.py's output will do),
// and ~/.cache/flower-semantic-audio/export is tried when it is unset. CI's
// Linux leg downloads it there and sets FLOWER_REQUIRE_CLAP_MODEL.
[Trait("Category", "RequiresClapModel")]
[Trait("Category", "RequiresFfmpeg")]
public class ClapEmbeddingReferenceTests : IClassFixture<SemanticAudioFixtures>
{
    private const float MinimumCosine = 0.999f;

    private static readonly SemanticAudioModelManifest Manifest = SemanticAudioModelManifest.Current;

    private readonly SemanticAudioFixtures _fixtures;

    public ClapEmbeddingReferenceTests(SemanticAudioFixtures fixtures) => _fixtures = fixtures;

    private static string ModelDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("FLOWER_CLAP_MODEL_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "flower-semantic-audio", "export");
        var present = Manifest.Files.All(f => File.Exists(Path.Combine(directory, f.Name)));
        var message = $"The model isn't in {directory}; set FLOWER_CLAP_MODEL_DIR to a folder holding the music_audioset_epoch_15_esc_90.14 release.";
        // CI's Linux leg downloads the model, and says so with this: there a
        // missing model is a broken step, not a reason to check nothing.
        if (Environment.GetEnvironmentVariable("FLOWER_REQUIRE_CLAP_MODEL") is { Length: > 0 })
            Assert.True(present, message);
        Assert.SkipUnless(present, message);
        return directory;
    }

    private static JsonElement References()
    {
        using var document = JsonDocument.Parse(SemanticAudioReferences.Read("embeddings.json"));
        return document.RootElement.Clone();
    }

    private static float[] Vector(JsonElement element) => element.EnumerateArray().Select(v => v.GetSingle()).ToArray();

    [Fact]
    public void Decoded_clips_embed_as_they_do_in_python()
    {
        var directory = ModelDirectory();
        using var encoder = new OnnxClapAudioEncoder(Path.Combine(directory, Manifest.AudioEncoder.Name));

        foreach (var clip in References().GetProperty("clips").EnumerateArray())
        {
            var fixture = clip.GetProperty("fixture").GetString()!;
            var window = clip.GetProperty("window").GetInt32();
            using var reader = new FfmpegClipSource().Open(_fixtures.PathOf(fixture));

            var embedding = encoder.Encode(ClipWaveform.Prepare(reader.ReadWindow(window)));

            var cosine = Embedding.Dot(embedding, Vector(clip.GetProperty("embedding")));
            Assert.True(cosine >= MinimumCosine, $"{fixture} window {window}: cosine {cosine}");
        }
    }

    [Fact]
    public void Pythons_own_waveforms_embed_the_same_here()
    {
        // The encoder alone, with decoding taken out of it: what Python fed
        // its session, fed to this one.
        var directory = ModelDirectory();
        using var encoder = new OnnxClapAudioEncoder(Path.Combine(directory, Manifest.AudioEncoder.Name));

        foreach (var clip in References().GetProperty("clips").EnumerateArray())
        {
            var ints = MemoryMarshal.Cast<byte, short>(SemanticAudioReferences.ReadGzipped(clip.GetProperty("prepared").GetString()!));
            var window = new float[ints.Length];
            for (var i = 0; i < ints.Length; i++)
                window[i] = ints[i] / 32767f;

            var embedding = encoder.Encode(ClipWaveform.Prepare(window));

            var cosine = Embedding.Dot(embedding, Vector(clip.GetProperty("embedding")));
            Assert.True(cosine >= 0.9999f, $"{clip.GetProperty("prepared").GetString()}: cosine {cosine}");
        }
    }

    [Fact]
    public void Prompts_embed_as_they_do_in_python()
    {
        var directory = ModelDirectory();
        var tokenizer = RobertaTokenizer.Load(Path.Combine(directory, Manifest.Vocabulary.Name), Path.Combine(directory, Manifest.Merges.Name));
        using var encoder = new OnnxClapTextEncoder(Path.Combine(directory, Manifest.TextEncoder.Name), tokenizer);
        var prompts = References().GetProperty("prompts").EnumerateArray().ToList();

        var embeddings = encoder.Encode(prompts.Select(p => string.Format(Clap.PromptTemplate, p.GetProperty("prompt").GetString())).ToList());

        for (var i = 0; i < prompts.Count; i++)
        {
            var cosine = Embedding.Dot(embeddings[i], Vector(prompts[i].GetProperty("embedding")));
            Assert.True(cosine >= MinimumCosine, $"\"{prompts[i].GetProperty("prompt").GetString()}\": cosine {cosine}");
        }
    }
}
