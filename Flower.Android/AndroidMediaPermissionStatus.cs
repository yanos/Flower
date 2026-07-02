using Android;
using Android.App;
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
    private readonly Activity _activity;

    public AndroidMediaPermissionStatus(Activity activity)
    {
        _activity = activity;
    }

    public bool IsGranted()
    {
        string permission = Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            ? Manifest.Permission.ReadMediaAudio!
            : Manifest.Permission.ReadExternalStorage!;

        return ContextCompat.CheckSelfPermission(_activity, permission) == Permission.Granted;
    }

    public void OpenAppSettings()
    {
        var intent = new Intent(Settings.ActionApplicationDetailsSettings);
        intent.SetData(Uri.FromParts("package", _activity.PackageName, null));
        intent.AddFlags(ActivityFlags.NewTask);
        _activity.StartActivity(intent);
    }
}
