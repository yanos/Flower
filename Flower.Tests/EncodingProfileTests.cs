using System;
using System.IO;
using System.Text;

using Flower.Importer;

using Xunit;

namespace Flower.Tests;

// Drives EncodingProfile off handcrafted first frames rather than real encoded
// files: what it parses is a fixed byte layout (the MPEG frame header, then
// Xing/Info, then LAME's own extension), and building that layout by hand is
// both smaller than checking in a set of MP3s and able to cover the cases a
// handful of sample files would not - a Xing header with no LAME extension, a
// non-LAME writer, a preset value nothing recognises.
public class EncodingProfileTests
{
    [Fact]
    public void Reports_the_encoder_and_the_V_preset_for_a_LAME_VBR_file()
    {
        // Method 4 is LAME's "VBR new/mtrh", which is what -V produces; 500 is
        // V0 in lame.h's preset_mode enum.
        var mp3 = Mp3(xing: true, encoder: "LAME3.100", vbrMethod: 4, preset: 500);

        Assert.Equal("LAME 3.100, VBR (V0)", EncodingProfile.DescribeMp3(mp3));
    }

    [Theory]
    [InlineData(500, "V0")]
    [InlineData(460, "V4")]
    [InlineData(410, "V9")]
    public void Maps_every_V_preset_back_to_its_own_number(int preset, string expected)
    {
        var mp3 = Mp3(xing: true, encoder: "LAME3.100", vbrMethod: 3, preset: preset);

        Assert.Equal($"LAME 3.100, VBR ({expected})", EncodingProfile.DescribeMp3(mp3));
    }

    [Fact]
    public void Names_the_alias_presets_rather_than_their_numbers()
    {
        var mp3 = Mp3(xing: true, encoder: "LAME3.99 ", vbrMethod: 4, preset: 1002);

        Assert.Equal("LAME 3.99, VBR (extreme)", EncodingProfile.DescribeMp3(mp3));
    }

    [Fact]
    public void Reports_a_CBR_files_bitrate_from_its_frame_header()
    {
        // Method 1 is CBR, and "Info" rather than "Xing" is how the encoder
        // says so a second time.
        var mp3 = Mp3(xing: false, encoder: "LAME3.100", vbrMethod: 1, preset: 0, bitrateIndex: 14);

        Assert.Equal("LAME 3.100, CBR 320 kbps", EncodingProfile.DescribeMp3(mp3));
    }

    [Fact]
    public void Reports_the_target_bitrate_of_an_ABR_run()
    {
        var mp3 = Mp3(xing: true, encoder: "LAME3.100", vbrMethod: 2, preset: 192);

        Assert.Equal("LAME 3.100, ABR 192 kbps", EncodingProfile.DescribeMp3(mp3));
    }

    // A Xing header with no LAME extension after it - every re-muxer that
    // rewrote the header, and plenty of non-LAME encoders. VBR is still known
    // and is worth saying; a preset is not.
    [Fact]
    public void Falls_back_to_VBR_alone_when_there_is_no_LAME_extension()
    {
        var mp3 = Mp3(xing: true, encoder: null, vbrMethod: 0, preset: 0);

        Assert.Equal("VBR", EncodingProfile.DescribeMp3(mp3));
    }

    [Fact]
    public void Says_nothing_for_a_stream_with_no_Xing_header_at_all()
    {
        var frame = new byte[256];
        WriteFrameHeader(frame, bitrateIndex: 9);

        Assert.Null(EncodingProfile.DescribeMp3(new MemoryStream(frame)));
    }

    [Fact]
    public void Says_nothing_for_bytes_that_are_not_an_MPEG_frame()
    {
        Assert.Null(EncodingProfile.DescribeMp3(new MemoryStream(new byte[256])));
    }

    [Fact]
    public void Skips_an_ID3v2_tag_to_find_the_first_frame()
    {
        var tagged = new byte[10 + 128 + 256];
        tagged[0] = (byte)'I';
        tagged[1] = (byte)'D';
        tagged[2] = (byte)'3';
        // Syncsafe 128: seven bits per byte, so 0x01 0x00 rather than 0x80.
        tagged[8] = 0x01;
        tagged[9] = 0x00;

        var frame = ((MemoryStream)Mp3(xing: true, encoder: "LAME3.100", vbrMethod: 4, preset: 500)).ToArray();
        Array.Copy(frame, 0, tagged, 10 + 128, frame.Length);

        Assert.Equal("LAME 3.100, VBR (V0)", EncodingProfile.DescribeMp3(new MemoryStream(tagged)));
    }

    [Fact]
    public void Only_looks_at_mp3s()
    {
        Assert.Null(EncodingProfile.Describe("/music/song.flac"));
        Assert.Null(EncodingProfile.Describe(null));
    }

    // One MPEG-1 Layer III stereo frame carrying a Xing (or Info) header and,
    // unless encoder is null, LAME's extension after it.
    private static Stream Mp3(bool xing, string? encoder, int vbrMethod, int preset, int bitrateIndex = 9)
    {
        var buffer = new byte[256];
        WriteFrameHeader(buffer, bitrateIndex);

        // MPEG-1 stereo: 32 bytes of side information between the header and
        // the Xing tag.
        var tag = 4 + 32;
        Encoding.ASCII.GetBytes(xing ? "Xing" : "Info").CopyTo(buffer, tag);
        // No optional fields, so the LAME extension starts immediately after
        // the four flag bytes.
        buffer[tag + 7] = 0x00;

        if (encoder != null)
        {
            var lame = tag + 8;
            Encoding.ASCII.GetBytes(encoder.PadRight(9)).AsSpan(0, 9).CopyTo(buffer.AsSpan(lame));
            buffer[lame + 0x09] = (byte)vbrMethod;
            buffer[lame + 0x14] = (byte)(vbrMethod == 2 && preset is > 0 and <= 255 ? preset : 0);
            buffer[lame + 0x1A] = (byte)(preset >> 8);
            buffer[lame + 0x1B] = (byte)(preset & 0xFF);
        }

        return new MemoryStream(buffer);
    }

    private static void WriteFrameHeader(byte[] buffer, int bitrateIndex)
    {
        buffer[0] = 0xFF;
        buffer[1] = 0xFB;                             // MPEG 1, Layer III, no CRC
        buffer[2] = (byte)((bitrateIndex << 4) | 0x00); // bitrate index, 44.1 kHz
        buffer[3] = 0x00;                             // stereo
    }
}
