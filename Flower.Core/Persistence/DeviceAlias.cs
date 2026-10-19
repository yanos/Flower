using System.Globalization;
using System.Text;

namespace Flower.Persistence
{
    // What a device says it is called, made fit to keep. The alias arrives in
    // X-Flower-Alias on every signed request and is the caller's own text: it
    // lands in trusted-peers.json, in this server's log lines and in the
    // operator's Devices list, so a device must not be able to write a
    // kilobyte there, start a new log line, or reverse the text around it.
    //
    // Control characters (a newline is how a log line is forged) and the
    // bidirectional overrides and isolates (how "iPhone" is made to read as
    // something else) are dropped; everything else, emoji and joiners
    // included, is a name somebody chose. Cut to MaxLength at a whole
    // character, so an emoji is never left half there.
    public static class DeviceAlias
    {
        public const int MaxLength = 64;

        // Null when nothing usable is left, which a caller reads the way it
        // reads a device that sent no alias at all.
        public static string? Clean(string? claimed)
        {
            if (string.IsNullOrEmpty(claimed))
                return null;

            var kept = new StringBuilder(claimed.Length);
            foreach (var c in claimed)
            {
                if (!char.IsControl(c) && !IsBidiControl(c))
                    kept.Append(c);
            }

            var text = kept.ToString().Trim();
            if (text.Length > MaxLength)
            {
                var cut = 0;
                var elements = StringInfo.GetTextElementEnumerator(text);
                while (elements.MoveNext() && elements.ElementIndex + elements.GetTextElement().Length <= MaxLength)
                    cut = elements.ElementIndex + elements.GetTextElement().Length;
                text = text[..cut].TrimEnd();
            }

            return text.Length == 0 ? null : text;
        }

        // U+200E/U+200F marks, U+202A-U+202E embeddings and overrides,
        // U+2066-U+2069 isolates.
        private static bool IsBidiControl(char c) =>
            c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩');
    }
}
