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

    // Python's own waveforms are held tighter than MinimumCosine, though not
    // to the 0.9999998 a Mac measures against its own references: another
    // CPU takes another ONNX Runtime kernel. With the reduce_range model an
    // i5-8259U without VNNI gave 0.99987 and up for these windows, where the
    // plain int8 model before it - whose kernel saturated on such a CPU -
    // gave 0.962 on CI's AMD EPYC 7763 (export-clap-onnx.py's to_int8).
    private const float OwnWaveformCosine = 0.9995f;

    // Named in every message, so a failure says which kind of CPU it ran on.
    private static readonly string Cpu = CpuDescription();

    private static string CpuDescription()
    {
        var lines = File.Exists("/proc/cpuinfo") ? File.ReadAllLines("/proc/cpuinfo") : [];
        var model = lines.FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
        var flags = lines.FirstOrDefault(l => l.StartsWith("flags", StringComparison.Ordinal))?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var vnni = flags.Contains("avx512_vnni") || flags.Contains("avx_vnni") ? ", VNNI" : flags.Length > 0 ? ", no VNNI" : "";
        return $"{model ?? RuntimeInformation.ProcessArchitecture.ToString()}{vnni}";
    }

    private static string ModelDirectory()
    {
        var directory = Environment.GetEnvironmentVariable("FLOWER_CLAP_MODEL_DIR") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "flower-semantic-audio", "export");
        var present = Manifest.Files.All(f => File.Exists(Path.Combine(directory, f.Name)));
        var message = $"The model isn't in {directory}; set FLOWER_CLAP_MODEL_DIR to a folder holding the files of {Manifest.BaseUrl}.";
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
            Assert.True(cosine >= MinimumCosine, $"{fixture} window {window}: cosine {cosine} ({Cpu})");
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
            Assert.True(cosine >= OwnWaveformCosine, $"{clip.GetProperty("prepared").GetString()}: cosine {cosine} ({Cpu})");
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
            Assert.True(cosine >= MinimumCosine, $"\"{prompts[i].GetProperty("prompt").GetString()}\": cosine {cosine} ({Cpu})");
        }
    }
}
