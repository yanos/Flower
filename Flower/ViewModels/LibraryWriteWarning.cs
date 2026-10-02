using System.Collections.Generic;

namespace Flower.ViewModels;

// What an administrator is told when the server cannot write to its music
// folders - the banner across the top of the browser page (MainView), fed from
// GET /api/admin/library by App.ReportBrowserPairing. The server says the same
// thing in its own log at each scan that finds it (LibraryWriteAccess).
//
// A statement of what will be refused rather than an error: a read-only music
// mount is a legitimate way to run a server, and everything a listener does
// works on one. It is the owner's device that would otherwise find out, one
// refused upload or tag edit at a time.
public static class LibraryWriteWarning
{
    // Null when there is nothing to say - every folder is writable, or the
    // server is too old to report it.
    public static string? For(IReadOnlyList<string>? unwritableFolders)
    {
        if (unwritableFolders is not { Count: > 0 })
            return null;

        var where = unwritableFolders.Count == 1
            ? $"its music folder ({unwritableFolders[0]})"
            : $"its music folders ({string.Join(", ", unwritableFolders)})";

        return $"This server cannot write to {where} - it may be mounted read-only. "
               + "Songs still play, but uploads from your devices, tag and artwork edits, moves and Delete Files will be refused.";
    }
}
