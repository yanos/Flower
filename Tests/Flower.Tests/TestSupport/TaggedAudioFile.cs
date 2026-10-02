using System;
using System.IO;

namespace Flower.Tests.TestSupport;

// A real audio file with real tags, at a path below a folder - what an upload
// needs at both ends: bytes to send, and something the server's own Importer
// can read back into the same title, artist and album the sending device had.
// A WAV, because that is the format the suite can make without an encoder
// (see SyntheticWav), tagged through the same TagLib# the importer reads with.
public static class TaggedAudioFile
{
    // seed makes two files with the same tags different bytes - a different
    // rip of the same song - which is what a name collision is made of.
    public static string Create(
        string root, string relativePath, string title, string artist = "Artist", string album = "Album", byte seed = 1)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        SyntheticWav.CreateFile(directory, Path.GetFileName(path), TimeSpan.FromMilliseconds(300), SyntheticWav.Marker(seed));

        using var file = TagLib.File.Create(path);
        file.Tag.Title = title;
        file.Tag.Performers = [artist];
        file.Tag.Album = album;
        file.Save();
        return path;
    }
}
