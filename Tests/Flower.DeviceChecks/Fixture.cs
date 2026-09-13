using System;
using System.IO;
using System.Reflection;

using Flower.Audio;

namespace Flower.DeviceChecks;

// One piece of music, in each of the formats a library actually holds.
//
// The same two seconds of 440Hz sine is encoded five ways, so a format that
// decodes to nothing stands out against four that did not. Which oracle
// applies is a property of the format rather than a choice per check: the
// lossless three must come back as sine.wav's own samples byte for byte, and
// the lossy two can only be asked whether they are audible and in tune.
public sealed record Fixture(string Name, string Extension, string ResourceName, bool Lossless, uint SampleRate)
{
    public static readonly Fixture Wav = new("WAV", "wav", "sine.wav", Lossless: true, 48000);
    public static readonly Fixture Flac = new("FLAC", "flac", "sine.flac", Lossless: true, 48000);
    public static readonly Fixture Alac = new("ALAC in m4a", "m4a", "sine-alac.m4a", Lossless: true, 48000);
    public static readonly Fixture Aac = new("AAC in m4a", "m4a", "sine-aac.m4a", Lossless: false, 48000);
    public static readonly Fixture Mp3 = new("MP3", "mp3", "sine.mp3", Lossless: false, 48000);

    // The same five at 44.1kHz, which is what a music library is actually
    // made of - CDs, the iTunes Store, and every AAC rip anyone owns. The
    // 48kHz set above is the pipeline's own rate and so never resamples,
    // which makes it the wrong thing to have been checking alone: the album
    // that started all of this is 44.1kHz AAC, and the resampler sits between
    // it and the ring on every single platform.
    public static readonly Fixture Wav44 = new("WAV at 44.1kHz", "wav", "sine-44k.wav", Lossless: true, 44100);
    public static readonly Fixture Flac44 = new("FLAC at 44.1kHz", "flac", "sine-44k.flac", Lossless: true, 44100);
    public static readonly Fixture Alac44 = new("ALAC in m4a at 44.1kHz", "m4a", "sine-44k-alac.m4a", Lossless: true, 44100);
    public static readonly Fixture Aac44 = new("AAC in m4a at 44.1kHz", "m4a", "sine-44k-aac.m4a", Lossless: false, 44100);
    public static readonly Fixture Mp3_44 = new("MP3 at 44.1kHz", "mp3", "sine-44k.mp3", Lossless: false, 44100);

    // MP3 and AAC last within each rate, so a run that is cut short has
    // already reported on the formats whose failure is provable rather than
    // approximate.
    public static readonly Fixture[] All =
    [
        Wav, Flac, Alac, Mp3, Aac,
        Wav44, Flac44, Alac44, Mp3_44, Aac44,
    ];

    public const double ToneHz = 440.0;
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(2);

    // Whether this one can be held to the fixture's own samples byte for
    // byte. Only when nothing legitimately alters them on the way: a lossy
    // codec does, and so does the resampler any rate but the pipeline's goes
    // through. Everything else is asked whether it is audible and in tune,
    // which PcmOracle.ToneMismatch makes a real question.
    public bool ByteExact => Lossless && SampleRate == GaplessFormat.SampleRate;

    // The samples every byte-exact fixture must decode back to, header
    // stripped.
    public static byte[] ExpectedPcm() => Read(Wav).AsSpan(44).ToArray();

    public byte[] Bytes() => Read(this);

    private static byte[] Read(Fixture fixture)
    {
        var assembly = typeof(Fixture).Assembly;
        var qualified = $"{assembly.GetName().Name}.Fixtures.{fixture.ResourceName}";
        using var stream = assembly.GetManifestResourceStream(qualified)
            ?? throw new InvalidOperationException($"the {fixture.Name} fixture is not embedded as {qualified}");

        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
