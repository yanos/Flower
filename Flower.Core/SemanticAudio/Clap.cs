using System;

namespace Flower.SemanticAudio;

// The facts about LAION's CLAP music checkpoint
// (music_audioset_epoch_15_esc_90.14) that every other piece here is built
// around (docs/VIBE-PLAYLIST-PLAN.md, "The model files"). None of them is a
// choice: the model was trained on 10 s of 48 kHz mono, its text side
// on RoBERTa's 77 tokens, and it embeds both into 512 dimensions.
public static class Clap
{
    public const int SampleRate = 48_000;

    // One clip, which is what the audio encoder takes: 10 s.
    public const int ClipSamples = 10 * SampleRate;

    public static readonly TimeSpan ClipLength = TimeSpan.FromSeconds(10);

    // A song's last grid window counts only if it holds at least this much.
    // Three seconds repeat-padded to ten is still mostly that song; less is
    // mostly the padding (see ClipGrid).
    public const int MinimumTailSamples = 3 * SampleRate;

    public const int Dimensions = 512;

    public const int Tokens = 77;

    // What a prompt is wrapped in before it is encoded. CLAP's captions were
    // sentences, and phase 0 found the template mattered more than anything
    // else it measured: this one tied or won on every prompt the owner marked,
    // and nearly doubled the fits for an activity ("Tokenizing").
    public const string PromptTemplate = "A song that sounds like {0}.";
}
