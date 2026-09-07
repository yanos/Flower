using System;

using Android;
using Android.Content;
using Android.Content.PM;
using Android.Provider;

using AndroidX.Core.Content;

using Flower.Services;

// Aliased because this file needs System for OperatingSystem, and both
// namespaces spell Uri. The one meant here is Android's.
using Uri = Android.Net.Uri;

namespace Flower.Android;

public class AndroidMediaPermissionStatus : IMediaPermissionStatus
{
    private readonly Context _context;

    public AndroidMediaPermissionStatus(Context context)
    {
        _context = context;
    }

    public bool IsGranted()
    {
        // OperatingSystem rather than Build.VERSION.SdkInt, which says the same
        // thing but only to a reader: the platform-compatibility analyzer knows
        // this form and not that one, so the SdkInt spelling reads as an
        // unguarded call to an API 33 constant. Same idiom as MainActivity.
        string permission = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? Manifest.Permission.ReadMediaAudio!
            : Manifest.Permission.ReadExternalStorage!;

        return ContextCompat.CheckSelfPermission(_context, permission) == Permission.Granted;
    }

    // NewTask is required here since _context may be the Application context
    // rather than an Activity (see AvaloniaAndroidApplication/CustomizeAppBuilder
    // in MainActivity.cs) - starting an Activity from a non-Activity Context is
    // only legal with this flag set.
    public void OpenAppSettings()
    {
        var intent = new Intent(Settings.ActionApplicationDetailsSettings);
        intent.SetData(Uri.FromParts("package", _context.PackageName, null));
        intent.AddFlags(ActivityFlags.NewTask);
        _context.StartActivity(intent);
    }
}
