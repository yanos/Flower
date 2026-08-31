using System;
using System.Linq;

using Flower.Models;

namespace Flower.Importer
{
    // What TagLib's Properties say about the audio stream itself, as opposed to
    // its tags - the Technical tab of Track Info, minus Duration (which is part
    // of Track.SyncKey and so must never be re-derived off a second reading of a
    // file; see LibraryDownloadService).
    //
    // Split out of Importer because a folder scan is no longer the only thing
    // that reads them: a downloaded file lives wherever LibraryDownloadService
    // put it, which on every platform is somewhere no configured library folder
    // covers, so no scan will ever visit it and it has to read its own.
    public readonly record struct AudioTechnicalProperties(
        int Bitrate,
        int SampleRate,
        int Channels,
        int BitsPerSample,
        string? Codec)
    {
        // Throws whatever TagLib throws for an unreadable/corrupt/DRM'd file -
        // the callers already have to handle that for the tag read anyway.
        public static AudioTechnicalProperties Read(string path)
        {
            using var file = TagLib.File.Create(path);
            return From(file.Properties);
        }

        public static AudioTechnicalProperties From(TagLib.Properties? props) => new(
            Bitrate: props?.AudioBitrate ?? 0,
            SampleRate: props?.AudioSampleRate ?? 0,
            Channels: props?.AudioChannels ?? 0,
            BitsPerSample: BitsPerSampleOf(props),
            Codec: CodecOf(props));

        public void ApplyTo(Track track)
        {
            track.Bitrate = Bitrate;
            track.SampleRate = SampleRate;
            track.Channels = Channels;
            track.BitsPerSample = BitsPerSample;
            track.Codec = Codec;
        }

        private static string? CodecOf(TagLib.Properties? props) =>
            props?.Codecs != null
                ? string.Join(", ", props.Codecs.Where(c => c != null).Select(c => c.Description).Where(d => !string.IsNullOrEmpty(d)))
                : null;

        // TagLib answers Properties.BitsPerSample only for a codec that implements
        // ILosslessAudioCodec, and its MPEG-4 sample entry does not - so every
        // ALAC file in a library reported a bit depth of 0 (an all-"-" Bit Depth
        // in Track Info's Technical tab) while carrying the real value one
        // property away, on IsoAudioSampleEntry.AudioSampleSize.
        //
        // Read for ALAC only, deliberately. Every MPEG-4 audio sample entry has
        // that field, AAC's included, where it is the legacy QuickTime samplesize
        // and reads a flat 16 whatever the encoder actually did. A lossy stream
        // has no bit depth to report at all, so trusting it there would replace
        // an honest "-" with a fabricated "16-bit" across most of a library.
        //
        // FLAC and WAV need none of this - both codecs are lossless in TagLib's
        // own type system, so the first branch already has their answer.
        private static int BitsPerSampleOf(TagLib.Properties? props)
        {
            if (props == null)
                return 0;

            if (props.BitsPerSample > 0)
                return props.BitsPerSample;

            foreach (var codec in props.Codecs)
            {
                if (codec is TagLib.Mpeg4.IsoAudioSampleEntry entry && IsAlac(entry))
                    return entry.AudioSampleSize;
            }

            return 0;
        }

        // IsoAudioSampleEntry.Description is "MPEG-4 Audio (<box type>)", and the
        // box type itself is not exposed - so the four characters it prints in
        // parentheses are the only handle on which codec an mp4 entry really is.
        private static bool IsAlac(TagLib.Mpeg4.IsoAudioSampleEntry entry) =>
            entry.Description?.Contains("(alac)", StringComparison.OrdinalIgnoreCase) == true;
    }
}
