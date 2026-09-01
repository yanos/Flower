using System;
using System.IO;
using System.Text;

namespace Flower.Importer
{
    // "What settings was this encoded with" - the Technical tab's Encoding row.
    //
    // Not something TagLib can answer. Its Properties describe the decoded
    // stream (bitrate, sample rate, channels), which for a VBR file is an
    // average it computed, and it says nothing at all about the encoder or the
    // preset. The answer lives in the Xing/Info + LAME header that encoders
    // write into the first frame of an MP3, which nothing in TagLib# surfaces -
    // so this parses that frame directly.
    //
    // MP3 only, deliberately. It is the one format in a normal library where
    // "which profile" is a real question with a real answer recorded in the
    // file: -V0 and 320 CBR are different decisions someone made, and the
    // encoder wrote down which. AAC has no equivalent record, and a lossless
    // file (FLAC/ALAC/WAV) has no profile to speak of - its compression level
    // changes the file size and nothing else about the audio. Those return
    // null, and Track Info hides the row rather than printing a dash, the same
    // way Bit Depth already does for a lossy stream.
    public static class EncodingProfile
    {
        // Null when the file is not an MP3, is unreadable, or carries no
        // Xing/Info header at all (a plain CBR stream from an old encoder - the
        // honest answer there is "nothing recorded", not a guess).
        public static string? Describe(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return null;

            if (!string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                return null;

            try
            {
                using var stream = File.OpenRead(path);
                return DescribeMp3(stream);
            }
            catch (Exception)
            {
                // Same posture as everywhere else that reads a real library's
                // files: an unreadable or truncated one is routine, and the
                // consequence here is one row missing from an info window.
                return null;
            }
        }

        // Public so a test can drive it off a handcrafted stream rather than
        // needing a real encoded MP3 on disk - the byte layout is the whole of
        // what this parses, and building one by hand covers cases a handful of
        // sample files would not.
        public static string? DescribeMp3(Stream stream)
        {
            // The tag is not part of the audio; the first frame starts after it.
            var audioStart = Id3v2Length(stream);

            // 4 header bytes + up to 32 of side info + 4 "Xing" + 4 flags +
            // 4+4+100+4 of optional Xing fields + 36 of LAME tag. Reading the
            // lot in one go keeps this to a single seek and read.
            var buffer = new byte[256];
            stream.Position = audioStart;
            var read = ReadFully(stream, buffer);
            if (read < 40)
                return null;

            var header = FrameHeader.Parse(buffer);
            if (header is not { } frame)
                return null;

            // Xing (VBR) or Info (CBR) - same structure, and which of the two
            // words is present is itself the encoder saying which it did.
            var tagOffset = 4 + frame.SideInfoSize;
            if (tagOffset + 8 > read)
                return null;

            var isXing = Matches(buffer, tagOffset, "Xing");
            if (!isXing && !Matches(buffer, tagOffset, "Info"))
                return null;

            var flags = ReadInt32(buffer, tagOffset + 4);
            var lameOffset = tagOffset + 8
                + ((flags & 0x01) != 0 ? 4 : 0)   // frame count
                + ((flags & 0x02) != 0 ? 4 : 0)   // byte count
                + ((flags & 0x04) != 0 ? 100 : 0) // seek table
                + ((flags & 0x08) != 0 ? 4 : 0);  // VBR quality

            // A Xing header with no LAME extension: some encoders (and every
            // re-muxer that rewrote one) write the header and stop there. VBR
            // vs CBR is still known, and is worth saying on its own.
            if (lameOffset + 0x1C > read)
                return isXing ? "VBR" : $"CBR {frame.Bitrate} kbps";

            var encoder = Encoding.ASCII.GetString(buffer, lameOffset, 9).TrimEnd('\0', ' ');

            // "LAME3.100" and, for the encoders that copied LAME's tag layout,
            // "Lavf58.29" / "Lavc" and friends. Anything that is not printable
            // ASCII means the bytes are not a LAME tag at all.
            if (!IsPrintable(encoder))
                return isXing ? "VBR" : $"CBR {frame.Bitrate} kbps";

            var method = buffer[lameOffset + 0x09] & 0x0F;
            var preset = ((buffer[lameOffset + 0x1A] << 8) | buffer[lameOffset + 0x1B]) & 0x07FF;
            var abrBitrate = buffer[lameOffset + 0x14];

            return $"{Pretty(encoder)}, {MethodText(method, preset, abrBitrate, isXing, frame.Bitrate)}";
        }

        // "LAME3.100" is how the tag stores it; "LAME 3.100" is how everything
        // that shows it to a person prints it.
        private static string Pretty(string encoder) =>
            encoder.Length > 4 && char.IsLetter(encoder[3]) && char.IsDigit(encoder[4])
                ? encoder[..4] + " " + encoder[4..]
                : encoder;

        // The VBR method nibble says how it was encoded; the preset field says
        // which named setting produced that. Both are needed: -V0 and a hand-
        // rolled VBR run report the same method and differ only in the preset,
        // and an ABR run records its target bitrate in a third place again.
        private static string MethodText(int method, int preset, int abrBitrate, bool isXing, int frameBitrate)
        {
            var named = PresetName(preset);

            return method switch
            {
                1 or 8 => $"CBR {frameBitrate} kbps",
                2 or 9 => named ?? $"ABR {(abrBitrate > 0 ? abrBitrate : frameBitrate)} kbps",
                3 or 4 or 5 or 6 => named is null ? "VBR" : $"VBR ({named})",
                // Method 0 is "unknown", which every non-LAME writer of this
                // tag leaves it at - fall back to what the Xing/Info word
                // already told us.
                _ => named ?? (isXing ? "VBR" : $"CBR {frameBitrate} kbps"),
            };
        }

        // LAME's own preset_mode enum (lame.h). V0..V9 are 500 down to 410 in
        // steps of ten; the named --preset aliases sit above 1000. Anything in
        // 8..320 is an ABR run recording its target bitrate here instead.
        private static string? PresetName(int preset) => preset switch
        {
            >= 410 and <= 500 when preset % 10 == 0 => $"V{(500 - preset) / 10}",
            1000 => "r3mix",
            1001 => "standard",
            1002 => "extreme",
            1003 => "insane",
            1004 => "standard, fast",
            1005 => "extreme, fast",
            1006 => "medium",
            1007 => "medium, fast",
            >= 8 and <= 320 => $"ABR {preset} kbps",
            _ => null,
        };

        // The first frame sits after any ID3v2 tag, whose size is four
        // syncsafe bytes (7 bits each) at offset 6, not counting the 10-byte
        // header itself or an optional 10-byte footer.
        private static long Id3v2Length(Stream stream)
        {
            var header = new byte[10];
            stream.Position = 0;
            if (ReadFully(stream, header) < 10)
                return 0;

            if (header[0] != 'I' || header[1] != 'D' || header[2] != '3')
                return 0;

            var size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            var hasFooter = (header[5] & 0x10) != 0;
            return 10 + size + (hasFooter ? 10 : 0);
        }

        // Just enough of an MPEG audio frame header to find where the Xing tag
        // starts, which depends on the version and channel mode.
        private readonly record struct FrameHeader(int Bitrate, int SideInfoSize)
        {
            private static readonly int[,] BitrateTable =
            {
                // MPEG 1, Layer III
                { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 },
                // MPEG 2/2.5, Layer III
                { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 },
            };

            public static FrameHeader? Parse(byte[] buffer)
            {
                // Frame sync: eleven set bits.
                if (buffer[0] != 0xFF || (buffer[1] & 0xE0) != 0xE0)
                    return null;

                var versionBits = (buffer[1] >> 3) & 0x03; // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
                var layerBits = (buffer[1] >> 1) & 0x03;   // 1 = Layer III
                if (versionBits == 1 || layerBits != 1)
                    return null;

                var isMpeg1 = versionBits == 3;
                var bitrateIndex = (buffer[2] >> 4) & 0x0F;
                var channelMode = (buffer[3] >> 6) & 0x03; // 3 = mono
                var isMono = channelMode == 3;

                return new FrameHeader(
                    Bitrate: BitrateTable[isMpeg1 ? 0 : 1, bitrateIndex],
                    // Where the Xing tag lives, straight from the spec's side
                    // information sizes.
                    SideInfoSize: isMpeg1 ? (isMono ? 17 : 32) : (isMono ? 9 : 17));
            }
        }

        private static bool Matches(byte[] buffer, int offset, string word)
        {
            for (var i = 0; i < word.Length; i++)
            {
                if (buffer[offset + i] != word[i])
                    return false;
            }

            return true;
        }

        private static int ReadInt32(byte[] buffer, int offset) =>
            (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

        private static bool IsPrintable(string value)
        {
            if (value.Length == 0)
                return false;

            foreach (var c in value)
            {
                if (c < ' ' || c > '~')
                    return false;
            }

            return true;
        }

        // Stream.Read is allowed to return fewer bytes than asked for, and a
        // short read here would silently truncate the header being parsed.
        private static int ReadFully(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                    break;
                total += read;
            }

            return total;
        }
    }
}
