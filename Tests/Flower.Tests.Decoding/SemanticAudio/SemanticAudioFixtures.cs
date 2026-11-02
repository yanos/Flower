using System;
using System.IO;

using Flower.Tests.Decoding.Scenarios;
using Flower.Tests.TestSupport;

namespace Flower.Tests.Decoding.SemanticAudio;

// The audio make-references.py wrote its references from, as files on disk
// for the decoder: the shared 440 Hz fixture at 44.1 kHz, and the two-chord
// WAV it generates (gzipped in the references).
public sealed class SemanticAudioFixtures : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "flower-semantic-fixtures-" + Guid.NewGuid().ToString("N"));

    public SemanticAudioFixtures()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(PathOf("sine-44k.flac"), Fixture.Flac44.Bytes());
        File.WriteAllBytes(PathOf("chord-44k.wav"), SemanticAudioReferences.ReadGzipped("chord-44k.wav.gz"));
    }

    // By the name embeddings.json gives each fixture.
    public string PathOf(string fixture) => Path.Combine(_directory, fixture);

    public void Dispose() => TempDirectory.DeleteWhenReleased(_directory);
}
