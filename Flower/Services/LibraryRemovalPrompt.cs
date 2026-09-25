using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// The words of "Remove from Library"'s confirmation, worked out once for the
// desktop dialog and the phone's sheet, so the two cannot disagree about what
// is about to happen - above all about whether the server is involved, which
// is the part a reader would not guess.
public sealed record LibraryRemovalPrompt(
    IReadOnlyList<Track> Tracks,
    string Title,
    string Message,
    // Null when there is no file anywhere to delete (a selection of
    // placeholders whose server this device cannot speak for), in which case
    // the checkbox is not shown at all.
    string? DeleteFilesLabel)
{
    // Null when nothing in the selection can be removed from here - the
    // caller hides its menu entry rather than offering one that does nothing.
    public static LibraryRemovalPrompt? For(LibraryRemovalService service, IReadOnlyList<Track> selection)
    {
        var tracks = service.Removable(selection);
        if (tracks.Count == 0)
            return null;

        var title = tracks.Count == 1
            ? $"Remove \"{tracks[0].Title ?? "this song"}\" from your library?"
            : $"Remove {tracks.Count} songs from your library?";

        var onServer = service.AnyOnPairedServer(tracks);
        var message = tracks.Count == 1
            ? "It will be taken out of your library and every playlist."
            : "They will be taken out of your library and every playlist.";
        if (onServer)
            message += tracks.Count == 1
                ? " Your server will remove it too, for everyone who listens to it."
                : " Your server will remove them too, for everyone who listens to it.";

        var withheld = selection.Count - tracks.Count;
        if (withheld > 0)
            message += withheld == 1
                ? " One of the songs selected belongs to your server and will stay: only its administrator can remove it."
                : $" {withheld} of the songs selected belong to your server and will stay: only its administrator can remove them.";

        string? label = null;
        if (service.HasFiles(tracks))
        {
            var local = tracks.Any(t => t.Path != null);
            label = (local, onServer) switch
            {
                (true, true) => "Also delete the files, here and on the server",
                (false, true) => "Also delete the files on the server",
                _ when FileTrash.IsSupported => $"Also move the files to the {FileTrash.Name}",
                _ => "Also delete the files from this device",
            };

            message += " Unless you delete them, the files stay where they are, and a rescan won't add them back.";

            // Where a deleted file ends up, which differs by device and which
            // decides whether a mistake can be undone. The server's is only
            // "where it has one": this device cannot know what the server
            // runs on, and a server whose music folder is mounted read-only
            // (docker-compose.yml's default) cannot move anything at all.
            if (local)
                message += FileTrash.IsSupported
                    ? $" Deleted files here go to the {FileTrash.Name}."
                    : " Deleted files here are gone for good - this device has no Trash to put them in.";
            if (onServer)
                message += " On the server they go to its Trash where it has one.";
        }

        return new LibraryRemovalPrompt(tracks, title, message, label);
    }
}
