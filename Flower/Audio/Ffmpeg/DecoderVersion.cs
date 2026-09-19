using System;
using System.Reflection;

using FFAudio;

using Flower.Services;

namespace Flower.Audio.Ffmpeg
{
    // "ffaudio 1.0.0 · FFmpeg 7.1.1", for the version line under the app's own
    // on the About window and the phone's settings. Which FFmpeg is playing is
    // the first thing to ask about a track that will not.
    public static class DecoderVersion
    {
        // Null when the façade will not load, since then nothing is decoding
        // to name.
        public static string? Display => Decoder.IsAvailable ? _display ??= Describe() : null;

        private static string? _display;

        private static string Describe()
        {
            var assembly = typeof(Decoder).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? assembly.GetName().Version?.ToString()
                ?? "unknown";
            return $"ffaudio {AppVersion.StripBuildMetadata(version)} · FFmpeg {FFmpegBuild.Version}";
        }
    }
}
