using System;
using System.IO;
using System.Runtime.InteropServices;

using Avalonia.Platform;

namespace Flower.Desktop;

// Avalonia's Window.Icon (see MainWindow.axaml) drives the window's own
// titlebar/proxy icon, but does not reliably reach the Dock tile for a
// process launched unbundled (no .icns/Info.plist app bundle exists for this
// project - see CROSS-PLATFORM-PLAN.md) - `dotnet run`/Rider both launch the
// raw executable this way, so without this the Dock falls back to some
// generic icon. Setting NSApplication.applicationIconImage directly is the
// one thing that reliably works regardless of bundling.
internal static class MacDockIcon
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
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
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
}
