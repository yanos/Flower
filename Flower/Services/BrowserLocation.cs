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
