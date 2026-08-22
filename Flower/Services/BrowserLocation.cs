using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.JavaScript;

namespace Flower.Services;

// What the browser page was opened with, and where it was served from.
//
// Same [JSImport] shape as WebAudioManager: a tiny wwwroot module, imported once
// in Flower.Web's Program.Main before the app boots, so every call here resolves
// against an already-loaded module. Nothing outside OperatingSystem.IsBrowser()
// may call any of this - the module does not exist on any other platform.
public static partial class BrowserLocation
{
    // Must match the name Flower.Web's Program.cs imports weblocation.js under.
    public const string ModuleName = "weblocation";

    private static partial class Interop
    {
        [JSImport("getHash", ModuleName)]
        public static partial string GetHash();

        [JSImport("clearHash", ModuleName)]
        public static partial void ClearHash();

        [JSImport("getOrigin", ModuleName)]
        public static partial string GetOrigin();
    }

    public static Uri Origin => new(Interop.GetOrigin());

    // Reads the fragment's key=value pairs and immediately erases it from the
    // address bar - see weblocation.js for why. Called once, at startup: a caller
    // that reads it twice gets nothing the second time, which is the point.
    public static IReadOnlyDictionary<string, string> TakeFragment()
    {
        var hash = Interop.GetHash().TrimStart('#');
        Interop.ClearHash();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in hash.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0)
                values[Uri.UnescapeDataString(pair[..separator])] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return values;
    }
}

// What the page was opened with, read once at startup.
//
// TakeFragment erases the fragment as it reads it, so exactly one caller may
// ever call it - this class is that caller, and everything else asks the
// container for the result. Before the browser had a library, the one caller
// was the settings overlay and it could read the fragment itself; now the same
// token is also the credential for the catalog and for minting stream tickets
// (see AdminSessionCredentials), so it has to be shared rather than consumed.
public sealed class BrowserSession
{
    // The server-minted admin session token, or null if the page was opened
    // without one - a tab someone navigated to by hand rather than through the
    // desktop client's "Server Settings..." button. Such a tab has no authority
    // at all: no catalog, no playback, no settings.
    public string? Token { get; private init; }

    // What the page was asked to show. Only "settings" means anything today,
    // and it is what keeps a session token from *implying* the settings overlay
    // now that a plain jukebox tab carries one too.
    public string? Page { get; private init; }

    public static BrowserSession FromPageUrl()
    {
        var fragment = BrowserLocation.TakeFragment();
        return new BrowserSession
        {
            Token = fragment.TryGetValue("admin", out var token) && !string.IsNullOrWhiteSpace(token) ? token : null,
            Page = fragment.GetValueOrDefault("page"),
        };
    }
}
