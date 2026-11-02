using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flower.SemanticAudio;

// RoBERTa's byte-level BPE, which is what laion_clap feeds CLAP's text
// encoder: Hugging Face's RobertaTokenizer called with padding="max_length",
// truncation=True and max_length=77 (docs/VIBE-PLAYLIST-PLAN.md,
// "Tokenizing"). Written here rather than taken from Microsoft.ML.Tokenizers
// because it is small, has no reflection in it for NativeAOT to trip on, and
// has exactly one job; RobertaTokenizerReferenceTests holds it to the ids
// Hugging Face's own tokenizer gives.
//
// The one thing left out: Hugging Face splits the special tokens' own
// spellings ("<s>", "<mask>", ...) out of the text as special tokens. Nobody
// asks for music by typing "<mask>", so here they are plain text.
public sealed partial class RobertaTokenizer
{
    public const int StartId = 0;
    public const int PadId = 1;
    public const int EndId = 2;
    public const int UnknownId = 3;

    private readonly Dictionary<string, int> _vocabulary;
    private readonly Dictionary<(string, string), int> _mergeRanks;
    private readonly Dictionary<string, int[]> _cache = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();

    // GPT-2's byte-to-character table: every byte gets a printable character,
    // so BPE runs over text and never sees a raw control byte or a space.
    private static readonly char[] ByteToChar = BuildByteToChar();

    private RobertaTokenizer(Dictionary<string, int> vocabulary, Dictionary<(string, string), int> mergeRanks)
    {
        _vocabulary = vocabulary;
        _mergeRanks = mergeRanks;
    }

    public static RobertaTokenizer Load(string vocabularyPath, string mergesPath) =>
        FromStreams(File.OpenRead(vocabularyPath), File.OpenRead(mergesPath));

    public static RobertaTokenizer FromStreams(Stream vocabulary, Stream merges)
    {
        var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        using (vocabulary)
        using (var document = JsonDocument.Parse(vocabulary))
        {
            foreach (var entry in document.RootElement.EnumerateObject())
                vocab[entry.Name] = entry.Value.GetInt32();
        }

        var ranks = new Dictionary<(string, string), int>();
        using (merges)
        using (var reader = new StreamReader(merges, Encoding.UTF8))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("#version", StringComparison.Ordinal) || line.Length == 0)
                    continue;

                var space = line.IndexOf(' ');
                if (space <= 0)
                    continue;

                ranks.TryAdd((line[..space], line[(space + 1)..]), ranks.Count);
            }
        }

        return new RobertaTokenizer(vocab, ranks);
    }

    // The ids for one text: <s>, the text's tokens (cut so the whole fits),
    // </s>, then <pad> up to `length`. The attention mask is 1 for every id
    // that is not padding.
    public (long[] Ids, long[] AttentionMask) Encode(string text, int length = Clap.Tokens)
    {
        var ids = new long[length];
        var mask = new long[length];
        Array.Fill(ids, PadId);

        var position = 0;
        ids[position] = StartId;
        mask[position++] = 1;

        foreach (var id in Tokenize(text))
        {
            if (position >= length - 1)
                break;

            ids[position] = id;
            mask[position++] = 1;
        }

        ids[position] = EndId;
        mask[position] = 1;
        return (ids, mask);
    }

    public IEnumerable<int> Tokenize(string text)
    {
        foreach (Match piece in PreTokenizer().Matches(text))
        {
            foreach (var id in EncodePiece(piece.Value))
                yield return id;
        }
    }

    private int[] EncodePiece(string piece)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(piece, out var cached))
                return cached;
        }

        var bytes = Encoding.UTF8.GetBytes(piece);
        var symbols = new List<string>(bytes.Length);
        foreach (var b in bytes)
            symbols.Add(ByteToChar[b].ToString());

        Merge(symbols);

        var ids = new int[symbols.Count];
        for (var i = 0; i < symbols.Count; i++)
            ids[i] = _vocabulary.TryGetValue(symbols[i], out var id) ? id : UnknownId;

        lock (_cacheLock)
        {
            // Bounded, since a server tokenizes whatever its listeners type.
            if (_cache.Count < 50_000)
                _cache[piece] = ids;
        }

        return ids;
    }

    // Byte-pair merging: repeatedly join the adjacent pair with the lowest
    // merge rank, every occurrence of it at once, until no pair has a rank.
    private void Merge(List<string> symbols)
    {
        while (symbols.Count > 1)
        {
            var bestRank = int.MaxValue;
            (string, string) best = default;
            for (var i = 0; i < symbols.Count - 1; i++)
            {
                if (_mergeRanks.TryGetValue((symbols[i], symbols[i + 1]), out var rank) && rank < bestRank)
                {
                    bestRank = rank;
                    best = (symbols[i], symbols[i + 1]);
                }
            }

            if (bestRank == int.MaxValue)
                return;

            var merged = new List<string>(symbols.Count);
            for (var i = 0; i < symbols.Count; i++)
            {
                if (i < symbols.Count - 1 && symbols[i] == best.Item1 && symbols[i + 1] == best.Item2)
                {
                    merged.Add(best.Item1 + best.Item2);
                    i++;
                }
                else
                {
                    merged.Add(symbols[i]);
                }
            }

            symbols.Clear();
            symbols.AddRange(merged);
        }
    }

    private static char[] BuildByteToChar()
    {
        var table = new char[256];
        var assigned = new bool[256];
        for (var b = '!'; b <= '~'; b++)
            Assign(b);
        for (var b = '¡'; b <= '¬'; b++)
            Assign(b);
        for (var b = '®'; b <= 'ÿ'; b++)
            Assign(b);

        var next = 0;
        for (var b = 0; b < 256; b++)
        {
            if (assigned[b])
                continue;

            table[b] = (char)(256 + next++);
        }

        return table;

        void Assign(char c)
        {
            table[c] = c;
            assigned[c] = true;
        }
    }

    // GPT-2's pre-tokenizer, which RoBERTa kept: English contractions, runs of
    // letters, of digits, or of anything else, each with one leading space
    // attached, and whitespace on its own.
    [GeneratedRegex(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+")]
    private static partial Regex PreTokenizer();
}
