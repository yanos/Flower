using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Flower.Persistence
{
    // Shared resolver for the app's writable settings/library/config directory, used
    // by AppSettingsStore, LibraryStore, PlaylistStore and ColumnVisibilityStore.
    internal static class AppDataDirectory
    {
        public static string Path
        {
            get
            {
                if (PlatformDataDirectory.Current is { } overridden)
                    return overridden;

                // iOS has no Application Support-equivalent reachable via SpecialFolder;
                // the sandboxed Documents folder (also used by Importer) is the only
                // writable location available.
                if (OperatingSystem.IsIOS())
                    return System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Flower");

                return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                    ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Library", "Application Support", "Flower")
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Flower");
            }
        }
    }
}
