using Android.App;
using Android.Content.PM;

using Avalonia;
using Avalonia.Android;

using Flower.Importer;
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

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        AndroidMediaStoreImporter.HandlePermissionResult(requestCode, grantResults);
    }
}
