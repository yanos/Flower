using System.Collections.Generic;

using Flower.Services;

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

    // The same thing from the owner's device, where it is found out: edits
    // made here that the server could not write, and files it had nowhere to
    // put (LibraryMirrorService.Unwritten). Both are still owed and go again
    // at every sync, which is said so that the remedy is fixing the server
    // rather than making the edit again.
    public static string? ForUnwritten(UnwrittenChanges? unwritten)
    {
        if (unwritten is not { } u || u.EditedSongs + u.WaitingFiles == 0)
            return null;

        var missing = new List<string>();
        if (u.EditedSongs > 0)
            missing.Add($"your edits to {Count(u.EditedSongs, "song")}");
        if (u.WaitingFiles > 0)
            missing.Add($"{Count(u.WaitingFiles, "file")} from this device");

        return $"{u.ServerAlias} cannot write to its music folder - it may be read-only - so it is missing {string.Join(" and ", missing)}. "
               + "They are kept here and sent again at each sync.";
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";
}
