using System;
using System.IO;
using System.Runtime.InteropServices;

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Flower.Platform;

// Avalonia's Window.Icon (see MainWindow.axaml) drives the window's own
// titlebar/proxy icon, but does not reliably reach the Dock tile for a
// process launched unbundled - `dotnet run`/Rider both launch the raw
// executable this way, so without this the Dock falls back to some generic
// icon. Setting NSApplication.applicationIconImage directly is the one thing
// that reliably works regardless of bundling, which is why it is still done
// from Flower.MacOS - a head that *does* produce a bundle with an .icns.
//
// Raw objc_msgSend rather than AppKit bindings, and in the shared library
// rather than in a head, because Flower.Desktop is plain net10.0 and has no
// AppKit to bind against - see docs/AIRPLAY-BLUETOOTH-PLAN.md for the wider
// version of that constraint. Guarded by OperatingSystem.IsMacOS(), so the
// Windows/Linux/mobile heads that also compile this file never reach the
// P/Invokes.
public static class MacDockIcon
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjCLibrary)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(ObjCLibrary)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMsg(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMsg(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendMsg(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS())
            return;

        using var stream = AssetLoader.Open(new Uri("avares://Flower/Assets/flower-icon.png"));
        using var source = new Bitmap(stream);
        using var rounded = RoundCorners(source);
        using var ms = new MemoryStream();
        rounded.Save(ms, new PngBitmapEncoderOptions());
        var bytes = ms.ToArray();

        var unmanagedBytes = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);

            var nsData = SendMsg(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"),
                unmanagedBytes, (IntPtr)bytes.Length);

            var nsImage = SendMsg(SendMsg(objc_getClass("NSImage"), sel_registerName("alloc")),
                sel_registerName("initWithData:"), nsData);
            if (nsImage == IntPtr.Zero)
                return;

            var sharedApp = SendMsg(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            SendMsg(sharedApp, sel_registerName("setApplicationIconImage:"), nsImage);
        }
        finally
        {
            Marshal.FreeHGlobal(unmanagedBytes);
        }
    }

    // iOS masks app icons into its rounded "squircle" shape automatically;
    // macOS does not do this for an image handed to setApplicationIconImage:,
    // it draws it as-is. Bake the same corner rounding in ourselves so the
    // Dock tile matches the mobile icon instead of showing sharp corners.
    // 0.223 approximates Apple's iOS/macOS icon corner-radius-to-width ratio.
    private static RenderTargetBitmap RoundCorners(Bitmap source)
    {
        var rect = new Rect(source.PixelSize.ToSize(1));
        var rtb = new RenderTargetBitmap(source.PixelSize);
        using var ctx = rtb.CreateDrawingContext();
        using (ctx.PushClip(new RoundedRect(rect, rect.Width * 0.223)))
        {
            ctx.DrawImage(source, rect);
        }

        return rtb;
    }
}
