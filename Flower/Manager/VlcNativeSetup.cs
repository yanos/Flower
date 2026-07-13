using System;
using System.IO;
using System.Runtime.InteropServices;

using LibVLCSharp.Shared;

namespace Flower.Manager
{
    public static class VlcNativeSetup
    {
        private static bool _initialized;
        private static readonly object _lock = new();

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized)
                    return;
                _initialized = true;

                if (!OperatingSystem.IsMacOS())
                {
                    if (OperatingSystem.IsLinux())
                    {
                        // Distro runtime packages only ship versioned sonames (libvlc.so.5);
                        // the unversioned libvlc.so requires libvlc-dev, so fall back to the
                        // versioned name when the default probe fails.
                        NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, ResolveLinuxLibVlc);
                    }

                    Core.Initialize();
                    return;
                }

                var vlcBase = "/Applications/VLC.app/Contents/MacOS";
                var vlcLib = $"{vlcBase}/lib";
                if (Directory.Exists(vlcLib))
                {
                    setenv("VLC_PLUGIN_PATH", $"{vlcBase}/plugins", 1);
                    NativeLibrary.Load(Path.Combine(vlcLib, "libvlccore.dylib"));
                }

                Core.Initialize(Directory.Exists(vlcLib) ? vlcLib : null);
            }
        }

        private static IntPtr ResolveLinuxLibVlc(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == "libvlc" && NativeLibrary.TryLoad("libvlc.so.5", assembly, searchPath, out var handle))
                return handle;

            return IntPtr.Zero;
        }

        [DllImport("libc")]
        private static extern int setenv(string name, string value, int overwrite);
    }
}
