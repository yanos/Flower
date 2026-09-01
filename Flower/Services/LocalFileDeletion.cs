using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// Keeps the context-menu policy separate from the actual deletion operation:
// LibraryDownloadService deliberately supports deleting any local file, while
// the UI needs to warn before deleting one that Flower did not fetch from a
// server and therefore cannot treat as disposable cached media.
internal static class LocalFileDeletion
{
    public static IReadOnlyList<Track> LocalFiles(IReadOnlyList<Track> tracks) =>
        tracks.Where(track => track.Path != null).ToList();

    public static bool RequiresWarning(IEnumerable<Track> tracks) =>
        tracks.Any(track => !track.IsLocallyDownloaded);
}
