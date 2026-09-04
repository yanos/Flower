using System;

using LibVLCSharp.Shared;

using Microsoft.Extensions.Logging;

namespace Flower.Audio;

// Forwards LibVLC's own log into Flower's, so a stream that will not open says
// why.
//
// Everything Flower knows about a failed decode today it learns from the
// outside: MediaPlayer raised EncounteredError, or the decoder drained having
// produced no audio. Neither says which module refused, or what it refused
// over - so diagnosing a platform-specific decode failure has meant reasoning
// backwards from HTTP access logs and guessing at the demuxer. That is how the
// streamed-AAC-on-iOS investigation was run, and it guessed wrong twice: first
// that LibVLC could not pick a demuxer for an extensionless URL (naming the
// demuxer outright changed nothing), then that the iOS build might not carry
// the mp4 plugin at all (it does - `nm` finds _vlc_entry__demux_mp4_mp4).
// LibVLC knew the answer to both the whole time and had no way to say it.
//
// Only Warning and Error are forwarded. LibVLC's callback receives every
// message including Debug, which for one track is thousands of lines - far too
// many to ship off a phone (ClientLogStore uploads these) and too many to read.
// Warnings are where a module says it is giving up and why: "MP4 plugin
// discarded", "cannot seek", "no suitable demux module".
//
// The module name is kept in the message because that is the whole value here -
// "mp4" vs "avformat" vs "access_http" is the finding, not the prose.
// A class rather than a static one only so it can name its own log category
// as ILogger<VlcDiagnosticLog> - it is never instantiated.
public sealed class VlcDiagnosticLog
{
    private VlcDiagnosticLog()
    {
    }

    public static void Attach(LibVLC libVLC, ILogger? logger)
    {
        if (logger == null)
            return;

        libVLC.Log += (_, e) =>
        {
            // These arrive on LibVLC's own threads, so nothing here may touch
            // UI state or take a lock the decode path holds - writing to
            // ILogger is the whole body deliberately.
            switch (e.Level)
            {
                case LibVLCSharp.Shared.LogLevel.Error:
                    logger.LogError("LibVLC [{Module}] {Message}", e.Module, e.Message);
                    break;
                case LibVLCSharp.Shared.LogLevel.Warning:
                    logger.LogWarning("LibVLC [{Module}] {Message}", e.Module, e.Message);
                    break;
            }
        };
    }
}
