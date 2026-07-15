using System;
using System.Diagnostics;
using System.IO;

namespace Flower.Services;

// Reveals a track's file in the OS's file manager - shared by MusicListView's
// row context menu (MainView.axaml.cs) and the album grid's expanded track
// row context menu (AlbumGridRowControl.axaml.cs) rather than duplicated
// between them, since it's the exact same platform-specific process launch
// either way.
public static class FileLocator
{
    public static void Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        if (OperatingSystem.IsMacOS())
            Process.Start("open", ["-R", path]);
        else if (OperatingSystem.IsWindows())
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            Process.Start("xdg-open", [Path.GetDirectoryName(path)!]);
    }
}
