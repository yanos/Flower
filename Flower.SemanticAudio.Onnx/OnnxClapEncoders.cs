using System;
using System.Collections.Generic;

using Microsoft.ML.OnnxRuntime;

namespace Flower.SemanticAudio;

// The exported graphs, run with ONNX Runtime on the CPU (docs/VIBE-PLAYLIST-PLAN.md,
// "The model files"). In a project of its own so that ONNX Runtime reaches
// only the heads that host the engine - see the project file.
internal static class ClapSessions
{
    // One inference thread unless asked for more, and sequential execution:
    // the analyzer must never make playback stutter on the same machine, nor
    // starve a NAS's streams (docs/VIBE-PLAYLIST-PLAN.md, "Polite").
    public static InferenceSession Open(string path, int threads)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        return new InferenceSession(path, options);
    }
}

public sealed class OnnxClapAudioEncoder : IClapAudioEncoder
{
    private static readonly string[] InputNames = ["waveform"];
    private static readonly string[] OutputNames = ["audio_embeds"];
    private static readonly long[] Shape = [1, Clap.ClipSamples];

    private readonly InferenceSession _session;
    private readonly RunOptions _run = new();

    public OnnxClapAudioEncoder(string modelPath, int threads = 1)
    {
        _session = ClapSessions.Open(modelPath, threads);
    }

    public float[] Encode(float[] waveform)
    {
        if (waveform.Length != Clap.ClipSamples)
            throw new ArgumentException($"A clip is {Clap.ClipSamples} samples, not {waveform.Length}.", nameof(waveform));

        using var input = OrtValue.CreateTensorValueFromMemory(waveform, Shape);
        using var outputs = _session.Run(_run, InputNames, [input], OutputNames);
        return Embedding.Normalized(outputs[0].GetTensorDataAsSpan<float>());
    }

    public void Dispose()
    {
        _run.Dispose();
        _session.Dispose();
    }
}

public sealed class OnnxClapTextEncoder : IClapTextEncoder
{
    private static readonly string[] InputNames = ["input_ids", "attention_mask"];
    private static readonly string[] OutputNames = ["text_embeds"];

    private readonly InferenceSession _session;
    private readonly RobertaTokenizer _tokenizer;
    private readonly RunOptions _run = new();

    public OnnxClapTextEncoder(string modelPath, RobertaTokenizer tokenizer, int threads = 1)
    {
        _session = ClapSessions.Open(modelPath, threads);
        _tokenizer = tokenizer;
    }

    public float[][] Encode(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
            return [];

        var ids = new long[texts.Count * Clap.Tokens];
        var mask = new long[texts.Count * Clap.Tokens];
        for (var i = 0; i < texts.Count; i++)
        {
            var (textIds, textMask) = _tokenizer.Encode(texts[i]);
            textIds.CopyTo(ids, i * Clap.Tokens);
            textMask.CopyTo(mask, i * Clap.Tokens);
        }

        long[] shape = [texts.Count, Clap.Tokens];
        using var idsValue = OrtValue.CreateTensorValueFromMemory(ids, shape);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(mask, shape);
        using var outputs = _session.Run(_run, InputNames, [idsValue, maskValue], OutputNames);

        var flat = outputs[0].GetTensorDataAsSpan<float>();
        var result = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
            result[i] = Embedding.Normalized(flat.Slice(i * Clap.Dimensions, Clap.Dimensions));
        return result;
    }

    public void Dispose()
    {
        _run.Dispose();
        _session.Dispose();
    }
}
