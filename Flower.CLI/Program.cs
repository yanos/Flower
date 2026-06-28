using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using LibVLCSharp.Shared;

if (args.Length == 0)
{
    Console.WriteLine("Usage: flower-cli <audio-file> [audio-file2 ...]");
    return 1;
}

var vlcBase = "/Applications/VLC.app/Contents/MacOS";
var vlcLib = $"{vlcBase}/lib";

if (Directory.Exists(vlcLib))
{
    // Environment.SetEnvironmentVariable goes through the CLR's own env table;
    // libvlc reads the OS-level environment via getenv(), so call setenv directly.
    setenv("VLC_PLUGIN_PATH", $"{vlcBase}/plugins", 1);
    // Pre-load libvlccore so its @rpath install-name is already in dyld's
    // cache when libvlc.dylib is loaded and tries to link it.
    NativeLibrary.Load(Path.Combine(vlcLib, "libvlccore.dylib"));
}

[DllImport("libc")]
static extern int setenv(string name, string value, int overwrite);

Core.Initialize(Directory.Exists(vlcLib) ? vlcLib : null);

using var libVlc = new LibVLC("--no-video");
using var player = new MediaPlayer(libVlc);

var queue = args.ToList();
int index = 0;

bool playNext()
{
    if (index >= queue.Count) return false;

    var path = queue[index++];
    if (!File.Exists(path))
    {
        Console.WriteLine($"File not found: {path}");
        return playNext();
    }

    using var media = new Media(libVlc, path, FromType.FromPath);
    player.Media = media;
    player.Play();
    Console.WriteLine($"[{index}/{queue.Count}] {Path.GetFileName(path)}");
    return true;
}

player.EndReached += (_, _) => ThreadPool.QueueUserWorkItem(_ => playNext());

if (!playNext())
{
    Console.WriteLine("No valid files to play.");
    return 1;
}

if (Console.IsInputRedirected)
{
    Thread.Sleep(Timeout.Infinite);
}
else
{
    Console.WriteLine("Controls: [space] pause/resume  [n] next  [q] quit");
    while (true)
    {
        var key = Console.ReadKey(intercept: true).Key;
        switch (key)
        {
            case ConsoleKey.Spacebar:
                if (player.IsPlaying) player.Pause();
                else player.Play();
                Console.WriteLine(player.IsPlaying ? "Resumed" : "Paused");
                break;
            case ConsoleKey.N:
                player.Stop();
                if (!playNext())
                {
                    Console.WriteLine("End of queue.");
                    goto done;
                }
                break;
            case ConsoleKey.Q:
                player.Stop();
                goto done;
        }
    }
}

done:
return 0;
