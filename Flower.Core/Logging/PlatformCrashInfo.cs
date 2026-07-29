using System.Collections.Generic;

namespace Flower.Logging
{
    // Android's MainActivity.CustomizeAppBuilder runs (and needs to query
    // ActivityManager.GetHistoricalProcessExitReasons synchronously) before
    // App.axaml.cs's OnFrameworkInitializationCompleted has called
    // AppLogging.Initialize() - logging from there directly would just get
    // discarded by AppLogging's before-Initialize NullLogger fallback. This
    // is the same pattern as PlatformMusicImporter/PlatformDataDirectory:
    // MainActivity stashes what it found here, and App.axaml.cs drains it
    // once logging is actually up.
    public static class PlatformCrashInfo
    {
        public static IReadOnlyList<string>? PendingAndroidExitReasons { get; set; }
    }
}
