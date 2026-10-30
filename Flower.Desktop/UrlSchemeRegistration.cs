using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

using Flower.Logging;
using Flower.Services;

namespace Flower.Desktop;

// Makes this executable the handler for flower:// links, so a pairing link a
// server's admin sends opens Flower when it is clicked, on Windows and Linux.
// macOS and the phones declare the scheme in their bundles (Info.plist, the
// intent filter on Flower.Android's MainActivity); these two platforms have no
// bundle that says it, so the app says it for itself, for the current user only.
//
// Done on every launch rather than once at install, because it names a path,
// and the path is what moves: an AppImage is wherever its file was put, and a
// build run from the IDE is a different executable from the installed one.
// Rewritten only when what is registered differs, so a launch that changes
// nothing writes nothing. Whichever Flower ran last is the one a link opens.
//
// The link reaches the new process as its first argument, and Program hands it
// on - to an instance already running (PairingLinkForwarder) or to this one's
// PairingLinkInbox.
static class UrlSchemeRegistration
{
    private const string Scheme = PairingInvite.Scheme;

    // Off the startup path, and after logging is up, which is why the delay:
    // a registration that failed is worth one line in the log, and before
    // App.OnFrameworkInitializationCompleted there is no log to write it to.
    public static void RegisterInBackground() => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            if (OperatingSystem.IsWindows())
                RegisterOnWindows();
            else if (OperatingSystem.IsLinux())
                RegisterOnLinux();
        }
        catch (Exception ex)
        {
            AppLogging.CreateLogger("Flower.UrlSchemeRegistration")
                .LogWarning(ex, "Could not register Flower as the handler for {Scheme}:// links", Scheme);
        }
    });

    // Velopack's uninstall hook: take the scheme back out, but only if it is
    // still this install's - another copy registered since owns it now.
    [SupportedOSPlatform("windows")]
    public static void UnregisterOnWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Scheme}\shell\open\command");
        if (key?.GetValue(null) is string command && command == WindowsCommand())
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{Scheme}", throwOnMissingSubKey: false);
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterOnWindows()
    {
        var command = WindowsCommand();
        using (var existing = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{Scheme}\shell\open\command"))
        {
            if (existing?.GetValue(null) as string == command)
                return;
        }

        using var scheme = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        scheme.SetValue(null, "URL:Flower pairing link");
        scheme.SetValue("URL Protocol", "");
        using (var icon = scheme.CreateSubKey("DefaultIcon"))
            icon.SetValue(null, $"\"{Executable()}\",0");
        using (var open = scheme.CreateSubKey(@"shell\open\command"))
            open.SetValue(null, command);

        AppLogging.CreateLogger("Flower.UrlSchemeRegistration")
            .LogInformation("Registered {Executable} as the handler for {Scheme}:// links", Executable(), Scheme);
    }

    private static string WindowsCommand() => $"\"{Executable()}\" \"%1\"";

    // A desktop entry of its own rather than the AppImage's, which the desktop
    // never reads unless something integrates it, and xdg-mime to make it the
    // default for the scheme.
    private static void RegisterOnLinux()
    {
        var applications = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
                ? dataHome
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "applications");
        const string entryName = "flower-pairing-link.desktop";
        var entryPath = Path.Combine(applications, entryName);

        var entry = new StringBuilder()
            .AppendLine("[Desktop Entry]")
            .AppendLine("Type=Application")
            .AppendLine("Name=Flower")
            .AppendLine($"Exec={DesktopEntryQuote(Executable())} %u")
            .AppendLine($"MimeType=x-scheme-handler/{Scheme};")
            .AppendLine("NoDisplay=true")
            .AppendLine("Terminal=false")
            .ToString();

        if (File.Exists(entryPath) && File.ReadAllText(entryPath) == entry)
            return;

        Directory.CreateDirectory(applications);
        File.WriteAllText(entryPath, entry);

        using var xdgMime = Process.Start(new ProcessStartInfo("xdg-mime", ["default", entryName, $"x-scheme-handler/{Scheme}"])
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        xdgMime?.WaitForExit(TimeSpan.FromSeconds(10));

        AppLogging.CreateLogger("Flower.UrlSchemeRegistration")
            .LogInformation("Registered {Executable} as the handler for {Scheme}:// links ({Entry})", Executable(), Scheme, entryPath);
    }

    // The AppImage itself rather than the binary inside it: that one lives in a
    // mount that is gone once this process exits.
    private static string Executable() =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage
            ? appImage
            : Environment.ProcessPath ?? throw new InvalidOperationException("This process has no path to register.");

    // The desktop entry spec's quoting for an Exec argument: a reserved
    // character inside the quotes takes a backslash, and the file's own string
    // escaping then doubles every backslash - so a literal one is four.
    private static string DesktopEntryQuote(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c == '\\')
                quoted.Append(@"\\\\");
            else if (c is '"' or '`' or '$')
                quoted.Append(@"\\").Append(c);
            else
                quoted.Append(c);
        }
        return quoted.Append('"').ToString();
    }
}
