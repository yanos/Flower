using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

using Avalonia;
using Avalonia.Android;

using Flower.Importer;
using Flower.Logging;
using Flower.Persistence;
using Flower.Services;

namespace Flower.Android;

// Avalonia 12 splits Android startup across two objects: an Application
// (Application.OnCreate runs before any Activity exists - this is where the
// AppBuilder is built and Avalonia's framework initialization completes) and
// an Activity (created afterward, purely for hosting the AvaloniaView).
// Platform hooks that only need a Context (PlatformDataDirectory,
// PlatformPermissions, PlatformMulticastLock, crash-exit-reason collection)
// are wired here; PlatformMusicImporter - the one hook below that genuinely
// needs a live Activity, for ActivityCompat.RequestPermissions' result
// callback (see AndroidMediaStoreImporter) - is wired from MainActivity
// instead. See Flower/App.axaml.cs's IActivityApplicationLifetime branch for
// how OnFrameworkInitializationCompleted defers its own bootstrap until
// MainActivity has had a chance to run.
[Application]
public class FlowerApplication(nint javaReference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<App>(javaReference, transfer)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        PlatformDataDirectory.Current = FilesDir!.AbsolutePath;
        PlatformPermissions.Current = new AndroidMediaPermissionStatus(this);
        PlatformMulticastLock.Current = new AndroidMulticastLockHolder(this);

        PlatformCrashInfo.PendingAndroidExitReasons = CollectCrashExitReasons();

        return base.CustomizeAppBuilder(builder)
            .WithFlowerFonts();
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
}

[Activity(
    Label = "Flower.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    // AvaloniaActivity.OnCreate (our base) calls InitializeAvaloniaView - which
    // invokes App.axaml.cs's deferred MainViewFactory/Bootstrap() - as the very
    // first thing it does, before its own base.OnCreate() even runs. Setting
    // PlatformMusicImporter.Current here, before calling base.OnCreate(), is
    // what guarantees it's already wired by the time Bootstrap() reads it.
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        PlatformMusicImporter.Current = new AndroidMediaStoreImporter(this);

        base.OnCreate(savedInstanceState);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidMediaStoreImporter.HandlePermissionResult(requestCode, grantResults);
    }
}
