using Android;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Provider;

using AndroidX.Core.Content;

using Flower.Services;

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
        string permission = Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
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
