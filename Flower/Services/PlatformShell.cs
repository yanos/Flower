using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Flower.Services;

// Handing a file, a folder or a link to the desktop's own shell - Finder,
// Explorer, or whatever xdg-open names - which is the one place Flower starts
// another program on the user's behalf. A phone has no file manager to show a
// file in (iOS sandboxes the app, Android does not let it start processes) and
// neither does a browser tab, so IsAvailable is false on all three and the
// caller hides the affordance rather than offering one that does nothing
// (docs/CODE-REVIEW-2026-09.md E7).
//
// One class rather than a service each head registers, because the two heads
// it is true on - Flower.Desktop and Flower.MacOS - would register the same
// code, and the three it is false on would register nothing.
public static class PlatformShell
{
    public static bool IsAvailable =>
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    // Shows a file selected in its folder. On Linux there is no portable way to
    // select it, so its folder is opened instead.
    public static bool Reveal(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        if (OperatingSystem.IsMacOS())
            return Start(new ProcessStartInfo("open") { ArgumentList = { "-R", path } });
        if (OperatingSystem.IsWindows())
            return Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        if (OperatingSystem.IsLinux())
            return Start(new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! } });
        return false;
    }

    // Opens a folder itself, rather than selecting it within its parent.
    public static bool OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            return false;

        if (OperatingSystem.IsMacOS())
            return Start(new ProcessStartInfo("open") { ArgumentList = { path } });
        if (OperatingSystem.IsWindows())
            return Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } });
        if (OperatingSystem.IsLinux())
            return Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path } });
        return false;
    }

    // Opens a link in the default browser. UseShellExecute is what makes
    // Windows hand a URL to its default handler at all.
    public static bool OpenUrl(string url)
    {
        if (OperatingSystem.IsMacOS())
            return Start(new ProcessStartInfo("open") { ArgumentList = { url } });
        if (OperatingSystem.IsWindows())
            return Start(new ProcessStartInfo(url) { UseShellExecute = true });
        if (OperatingSystem.IsLinux())
            return Start(new ProcessStartInfo("xdg-open") { ArgumentList = { url } });
        return false;
    }

    // A Linux desktop without xdg-open is the realistic way for this to fail,
    // and a click is not worth an unhandled exception.
    private static bool Start(ProcessStartInfo startInfo)
    {
        try
        {
            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
