using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// What deleting a song's local file costs, on every head. LibraryDownloadService
// carries it out; the menus use this to decide what to warn about first.
//
// Only the paired server counts as somewhere the song can come back from. A
// download outlives the server it came from: IsLocallyDownloaded stays set
// after an unpairing or after the server stops listing the song, when the file
// on this device has become the only copy.
internal static class LocalFileDeletion
{
    public static IReadOnlyList<Track> LocalFiles(IReadOnlyList<Track> tracks) =>
        tracks.Where(track => track.Path != null).ToList();

    public static bool ServerHasCopy(Track track, string? pairedServerFingerprint) =>
        pairedServerFingerprint != null && track.OriginDeviceFingerprint == pairedServerFingerprint;

    // A copy Flower fetched from a server that still has the song: cache, safe
    // to delete outright and without asking.
    public static bool IsDisposable(Track track, string? pairedServerFingerprint) =>
        track.IsLocallyDownloaded && ServerHasCopy(track, pairedServerFingerprint);

    public static bool RequiresWarning(IEnumerable<Track> tracks, string? pairedServerFingerprint) =>
        tracks.Any(track => !IsDisposable(track, pairedServerFingerprint));

    // The desktop prompt, which always has a trash to name.
    public static string WarningMessage(IReadOnlyList<Track> tracks, string? pairedServerFingerprint)
    {
        var one = tracks.Count == 1;
        if (tracks.Any(track => !ServerHasCopy(track, pairedServerFingerprint)))
        {
            if (pairedServerFingerprint == null)
            {
                return one
                    ? $"This is your only copy. The file moves to the {FileTrash.Name} and the song leaves your library."
                    : $"These are your only copies. The files move to the {FileTrash.Name} and the songs leave your library.";
            }

            return one
                ? $"Your server doesn't have this song, so this is your only copy. The file moves to the {FileTrash.Name} and the song leaves your library."
                : $"Your server doesn't have some of these songs, so these are your only copies. Those files move to the {FileTrash.Name} and those songs leave your library.";
        }

        return one
            ? $"This is your own file rather than a download. It moves to the {FileTrash.Name}, and the song keeps playing from your server."
            : $"Some of these are your own files rather than downloads. They move to the {FileTrash.Name}, and the songs keep playing from your server.";
    }
}
