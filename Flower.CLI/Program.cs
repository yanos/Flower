using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Importer;
using Flower.Logging;
using Flower.Manager;
using Flower.Models;
using Flower.Persistence;

using LibVLCSharp.Shared;

VlcNativeSetup.Initialize();
using var libVlc = new LibVLC("--no-video");
using var player = new MediaPlayer(libVlc);

// --- Build play queue ---
// File mode: all args exist as paths on disk → play them directly.
// Library mode: no args → play full library; args → search library by title/artist/album.
List<Track?> queue;

bool fileMode = args.Length > 0 && args.All(File.Exists);

if (fileMode)
{
    queue = args.Select(p => (Track?)new Track { Title = Path.GetFileNameWithoutExtension(p), Path = p }).ToList();
}
else
{
    Console.Write("Loading library...");
    var store = new LibraryStore(AppLogging.CreateTypedLogger<LibraryStore>());
    var tracks = await store.LoadAsync();

    if (tracks.Count == 0)
    {
        Console.WriteLine(" scanning music folder...");
        var importer = new Importer();
        var settings = new AppSettingsStore().Load();
        tracks = importer.Import(settings.LibraryPaths);
        if (tracks.Count == 0)
        {
            Console.WriteLine("No tracks found.");
            return 1;
        }
        _ = store.SaveAsync(tracks);
    }

    Console.WriteLine($" {tracks.Count} tracks.");

    if (args.Length > 0)
    {
        var query = string.Join(" ", args).ToLowerInvariant();
        tracks = tracks.Where(t =>
            (t.Title?.ToLowerInvariant().Contains(query) ?? false) ||
            (t.Artists?.ToLowerInvariant().Contains(query) ?? false) ||
            (t.Album?.ToLowerInvariant().Contains(query) ?? false)
        ).ToList();

        if (tracks.Count == 0)
        {
            Console.WriteLine($"No tracks matching \"{string.Join(" ", args)}\".");
            return 1;
        }
        Console.WriteLine($"{tracks.Count} matching track(s).");
    }

    queue = tracks.Cast<Track?>().ToList();
}

// --- Playback ---
int index = 0;

bool playNext()
{
    while (index < queue.Count)
    {
        var track = queue[index++]!;
        if (!File.Exists(track.Path))
        {
            Console.WriteLine($"Not found, skipping: {track.Path}");
            continue;
        }
        using var media = new Media(libVlc, track.Path, FromType.FromPath);
        player.Media = media;
        player.Play();

        var label = string.IsNullOrWhiteSpace(track.Artists)
            ? track.Title ?? Path.GetFileName(track.Path)
            : $"{track.Title} — {track.Artists}";
        Console.WriteLine($"[{index}/{queue.Count}] {label}");
        return true;
    }
    return false;
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
                if (player.IsPlaying)
                    player.Pause();
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
