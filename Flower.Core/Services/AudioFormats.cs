using System;
using System.Collections.Generic;

namespace Flower.Services;

// Extensions accepted by scans/uploads and the HTTP types served for them.
public static class AudioFormats
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mp3"] = "audio/mpeg",
        ["m4a"] = "audio/mp4",
        ["wav"] = "audio/wav",
        ["flac"] = "audio/flac",
        ["alac"] = "audio/mp4",
        ["ogg"] = "audio/ogg",
        ["opus"] = "audio/ogg",
        ["aiff"] = "audio/aiff",
        ["aif"] = "audio/aiff",
        ["ape"] = "audio/x-ape",
        ["dsf"] = "audio/x-dsf",
    };

    public static bool SupportsExtension(string extension) => Types.ContainsKey(extension.TrimStart('.'));
    public static string ContentType(string extension) => Types.GetValueOrDefault(extension.TrimStart('.'), "application/octet-stream");
}
