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

    // What this device calls its trash, or null on a phone, which has none
    // and deletes outright (LibraryRemoval.TrashOrDelete).
    public static string? Trash => FileTrash.IsSupported ? FileTrash.Name : null;

    // The confirmation's text, on every head: the desktop asks only when
    // RequiresWarning, the phone always. trash is Trash, given so a test can
    // ask for either.
    public static string WarningMessage(IReadOnlyList<Track> tracks, string? pairedServerFingerprint, string? trash)
    {
        var one = tracks.Count == 1;
        if (tracks.Any(track => !ServerHasCopy(track, pairedServerFingerprint)))
        {
            if (pairedServerFingerprint == null)
            {
                return one
                    ? $"This is your only copy. {(trash != null ? $"The file moves to the {trash} and" : "The file is deleted for good, and")} the song leaves your library."
                    : $"These are your only copies. {(trash != null ? $"The files move to the {trash} and" : "The files are deleted for good, and")} the songs leave your library.";
            }

            return one
                ? $"Your server doesn't have this song, so this is your only copy. {(trash != null ? $"The file moves to the {trash} and" : "The file is deleted for good, and")} the song leaves your library."
                : $"Your server doesn't have some of these songs, so these are your only copies. {(trash != null ? $"Those files move to the {trash} and" : "Those files are deleted for good, and")} those songs leave your library.";
        }

        if (!RequiresWarning(tracks, pairedServerFingerprint))
        {
            return one
                ? "This removes the downloaded copy from this device. Your server still has it, so you can download it again later."
                : "This removes the downloaded copies from this device. Your server still has them, so you can download them again later.";
        }

        return one
            ? $"This is your own file rather than a download. {(trash != null ? $"It moves to the {trash}" : "It is deleted for good")}, and the song keeps playing from your server."
            : $"Some of these are your own files rather than downloads. {(trash != null ? $"They move to the {trash}" : "They are deleted for good")}, and the songs keep playing from your server.";
    }
}
