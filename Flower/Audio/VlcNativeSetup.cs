using System;
using System.IO;
using System.Runtime.InteropServices;

using LibVLCSharp.Shared;

using Microsoft.Extensions.Logging;

using Flower.Logging;

namespace Flower.Audio
{
    public static class VlcNativeSetup
    {
        private static bool _initialized;
        private static readonly object _lock = new();

        // Static class, no constructor to inject into - AppLogging's hatch, the
        // same as RubberBandScroll's. Every caller runs this after
        // AppLogging.Initialize (App.axaml.cs's Bootstrap), so these lines
        // land in the real log.
        private static readonly ILogger Logger =
            AppLogging.CreateLogger(typeof(VlcNativeSetup).FullName!);

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
                    Logger.LogDebug("Using the VLC install at {VlcLib}.", vlcLib);
                }
                else
                {
                    // Written before the call that is about to fail, not after -
                    // there is no after. Core.Initialize with nothing to
                    // initialize against takes the process down where it stands,
                    // so this line is the only thing that will exist to explain
                    // a startup crash on a Mac without VLC.app installed.
                    Logger.LogCritical(
                        "No VLC install found at {VlcLib}. Flower needs VLC.app on macOS and is about to fail to start; "
                        + "install it from videolan.org.", vlcLib);
                }

                Core.Initialize(Directory.Exists(vlcLib) ? vlcLib : null);
            }
        }

        private static IntPtr ResolveLinuxLibVlc(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName == "libvlc" && NativeLibrary.TryLoad("libvlc.so.5", assembly, searchPath, out var handle))
            {
                Logger.LogDebug("Resolved libvlc to the versioned soname libvlc.so.5.");
                return handle;
            }

            // Zero hands the probe back to the default resolver, which may still
            // succeed - so this is not yet a failure, only the fallback not
            // being the answer. Trace: the resolver is consulted for every
            // P/Invoke name the assembly uses, not just libvlc.
            Logger.LogTrace("No versioned libvlc fallback for {LibraryName}; deferring to the default probe.", libraryName);
            return IntPtr.Zero;
        }

        [DllImport("libc")]
        private static extern int setenv(string name, string value, int overwrite);
    }
}
