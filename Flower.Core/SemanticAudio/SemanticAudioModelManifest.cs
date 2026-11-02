using System;
using System.Collections.Generic;

namespace Flower.SemanticAudio;

public sealed record SemanticAudioModelFile(string Name, long Size, string Sha256);

// Exactly which model files this build loads, and from where
// (docs/VIBE-PLAYLIST-PLAN.md, "The model files"). They are assets of this
// repo's release tagged with the model's own name, written by
// scripts/semantic-audio/export-clap-onnx.py. The binary pins every file's
// size and SHA-256, so a new export is a new release and a change here -
// never a file swapped under a running install.
public sealed record SemanticAudioModelManifest(
    Uri BaseUrl,
    string AnalysisModel,
    SemanticAudioModelFile AudioEncoder,
    SemanticAudioModelFile TextEncoder,
    SemanticAudioModelFile Vocabulary,
    SemanticAudioModelFile Merges)
{
    public static SemanticAudioModelManifest Current { get; } = new(
        new Uri("https://github.com/yanos/Flower/releases/download/music_audioset_epoch_15_esc_90.14/"),
        // What semantic_audio_songs.model records: a song heard by any other
        // export is stale and heard again.
        "music_audioset_epoch_15_esc_90.14/int8",
        new("music_audioset_epoch_15_esc_90.14-audio-int8.onnx", 80_630_874, "1a701db76bb599a46a08fee2be104851763a3aefe54cba610ee7c64368e98e82"),
        new("music_audioset_epoch_15_esc_90.14-text-fp16.onnx", 250_930_215, "b791f3893ab9204fd831c271964e9e77deb853f7d5ee7efd7276658f5e937af1"),
        new("vocab.json", 999_355, "32b29acf82d3333462eb4b13416760f2aef956052e8fea1749fe5b20f866a4bf"),
        new("merges.txt", 456_318, "1ce1664773c50f3e0cc8842619a93edc4624525b728b188a9e0be33b7726adc5"));

    public IReadOnlyList<SemanticAudioModelFile> Files => [AudioEncoder, TextEncoder, Vocabulary, Merges];

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var file in Files)
                total += file.Size;
            return total;
        }
    }
}
