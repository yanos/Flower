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
// that reliably works regardless of bundling.
//
// Flower.MacOS does it too, although its bundle carries the same icon in an
// asset catalog (the csproj's AppIcon) for Finder, Launchpad and the dmg. The
// running Dock tile goes through the icon cache, keyed by bundle id and
// version, and a development build is always com.yanos.flower version 1 -
// like every older build and installed copy on the machine. A cache entry
// from one of those once gave the running app a blank placeholder tile; an
// image set here is not looked up.
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
        using var tile = OnIconGrid(source);
        using var ms = new MemoryStream();
        tile.Save(ms, new PngBitmapEncoderOptions());
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

    // macOS draws an image handed to setApplicationIconImage: edge to edge,
    // and does not round it the way iOS masks its icons. A Mac icon is
    // neither: on Apple's grid the tile is 824 of a 1024 canvas, centred,
    // with the rest left for its drop shadow - full-bleed art stands a fifth
    // bigger than every other icon in the Dock. flower-icon.png already has
    // its corners rounded (for the Windows and Linux window icon), so this
    // only shrinks it onto the grid and puts the shadow under it.
    private const int Canvas = 1024;
    private const double Tile = 824;
    private const double CornerRatio = 0.2237;

    private static RenderTargetBitmap OnIconGrid(Bitmap source)
    {
        var inset = (Canvas - Tile) / 2;
        var tile = new Rect(inset, inset, Tile, Tile);
        var shape = new RoundedRect(tile, Tile * CornerRatio);
        var rtb = new RenderTargetBitmap(new PixelSize(Canvas, Canvas));
        using var ctx = rtb.CreateDrawingContext();
        // Apple's template shadow: straight down, soft, faint.
        ctx.DrawRectangle(Brushes.White, null, shape,
            new BoxShadows(new BoxShadow { OffsetY = 10, Blur = 20, Color = Color.FromArgb(0x4D, 0, 0, 0) }));
        using (ctx.PushClip(shape))
        {
            ctx.DrawImage(source, tile);
        }

        return rtb;
    }
}
