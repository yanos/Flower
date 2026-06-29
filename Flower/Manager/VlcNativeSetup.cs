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
                if (_initialized) return;
                _initialized = true;

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

        [DllImport("libc")]
        private static extern int setenv(string name, string value, int overwrite);
    }
}
