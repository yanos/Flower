using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Flower.SemanticAudio;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.SemanticAudio;

// The tokenizer against Hugging Face's RobertaTokenizer, called the way
// laion_clap calls it, on prompts chosen to be awkward: accents, emoji,
// scripts other than Latin, odd whitespace, contractions, and a prompt long
// enough to be cut. A wrong id is a prompt CLAP misreads, silently, so the
// ids have to be exactly Hugging Face's (docs/VIBE-PLAYLIST-PLAN.md,
// "Tokenizing").
//
// Against the part of the vocabulary those prompts use (make-references.py
// explains why that is enough); ClapEmbeddingReferenceTests runs the real
// files.
public class RobertaTokenizerReferenceTests
{
    private static RobertaTokenizer Tokenizer() => RobertaTokenizer.FromStreams(
        SemanticAudioReferences.Open("vocab.subset.json"),
        SemanticAudioReferences.Open("merges.subset.txt"));

    private static List<(string Text, long[] Ids)> References()
    {
        using var document = JsonDocument.Parse(SemanticAudioReferences.Read("tokens.json"));
        return document.RootElement.EnumerateArray()
            .Select(e => (e.GetProperty("text").GetString()!, e.GetProperty("ids").EnumerateArray().Select(i => i.GetInt64()).ToArray()))
            .ToList();
    }

    [Fact]
    public void Gives_hugging_faces_ids_for_every_reference_prompt()
    {
        var tokenizer = Tokenizer();
        var references = References();
        Assert.True(references.Count >= 30);

        var wrong = new List<string>();
        foreach (var (text, expected) in references)
        {
            var (ids, _) = tokenizer.Encode(text);
            if (!ids.SequenceEqual(expected))
                wrong.Add($"\"{text}\": expected [{string.Join(", ", expected)}], got [{string.Join(", ", ids)}]");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void Masks_exactly_the_ids_that_are_not_padding()
    {
        var tokenizer = Tokenizer();
        foreach (var (text, _) in References())
        {
            var (ids, mask) = tokenizer.Encode(text);
            var real = ids.TakeWhile(id => id != RobertaTokenizer.PadId).Count();
            Assert.Equal(Enumerable.Repeat(1L, real).Concat(Enumerable.Repeat(0L, Clap.Tokens - real)), mask);
        }
    }
}
