using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Android.App;
using Android.Content;
using Android.Content.PM;

using Avalonia;
using Avalonia.Android;

using Flower.Importer;
using Flower.Logging;
using Flower.Persistence;
using Flower.Services;

namespace Flower.Android;

[Activity(
    Label = "Flower.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        PlatformMusicImporter.Current = new AndroidMediaStoreImporter(this);
        PlatformDataDirectory.Current = FilesDir!.AbsolutePath;
        PlatformPermissions.Current = new AndroidMediaPermissionStatus(this);
        PlatformMulticastLock.Current = new AndroidMulticastLockHolder(this);

        PlatformCrashInfo.PendingAndroidExitReasons = CollectCrashExitReasons();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    // A native crash (e.g. inside libvlc.so) bypasses .NET's own exception
    // handling entirely - Android's own record of why the previous process
    // died (ApplicationExitInfo, API 30+) is the only way to learn about it
    // without a third-party native-crash library. Stashed via
    // PlatformCrashInfo rather than logged directly - AppLogging hasn't been
    // initialized yet at this point in startup, see its own doc comment.
    private IReadOnlyList<string>? CollectCrashExitReasons()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
            return null;

        if (GetSystemService(Context.ActivityService) is not ActivityManager activityManager)
            return null;

        var exitInfos = activityManager.GetHistoricalProcessExitReasons(PackageName, 0, 0);
        if (exitInfos == null || exitInfos.Count == 0)
            return null;

        var markerPath = Path.Combine(AppLogging.LogsDirectory, "android-crash-scan.marker");
        var since = ReadMarker(markerPath);
        var newest = since;
        var reasons = new List<string>();

        foreach (var info in exitInfos)
        {
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(info.Timestamp);
            if (timestamp <= since)
                continue;
            if (info.Reason != (int)ApplicationExitInfoReason.CrashNative && info.Reason != (int)ApplicationExitInfoReason.Crash)
                continue;

            reasons.Add($"{timestamp:O} reason={info.Reason} description={info.Description}");
            if (timestamp > newest)
                newest = timestamp;
        }

        WriteMarker(markerPath, newest);
        return reasons;
    }

    private static DateTimeOffset ReadMarker(string path)
    {
        try
        {
            if (File.Exists(path) &&
                DateTimeOffset.TryParse(File.ReadAllText(path), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since))
                return since;
        }
        catch
        {
            // Best effort - fall through to "the beginning of time" below, which just means every crash in Android's (short) retained history gets logged once.
        }
        return DateTimeOffset.MinValue;
    }

    private static void WriteMarker(string path, DateTimeOffset timestamp)
    {
        try
        {
            Directory.CreateDirectory(AppLogging.LogsDirectory);
            File.WriteAllText(path, timestamp.ToString("o", CultureInfo.InvariantCulture));
        }
        catch
        {
            // Best effort - if this fails we just rescan the same window next time.
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidMediaStoreImporter.HandlePermissionResult(requestCode, grantResults);
    }
}
