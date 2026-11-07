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
        // The second export of this checkpoint. The first's audio encoder
        // was plain int8, which an x64 CPU without VNNI saturated on (cosine
        // 0.939 against fp32 on an i5-8259U); this one quantized with
        // reduce_range (export-clap-onnx.py's to_int8).
        new Uri("https://github.com/yanos/Flower/releases/download/music_audioset_epoch_15_esc_90.14-2/"),
        // What semantic_audio_songs.model records: a song heard by any other
        // export is stale and heard again.
        "music_audioset_epoch_15_esc_90.14/int8-reduce-range",
        // Its own name, not the first export's: a device knows the model by
        // file name and size (SemanticAudioModelStore.IsPresent), and the two
        // are the same size to the byte.
        new("music_audioset_epoch_15_esc_90.14-audio-int8-reduce-range.onnx", 80_630_874, "6c6f27d8ed52ae66f15db3caf2ad24472540165c6b2c2866c3d8a49dcac0c1ee"),
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
