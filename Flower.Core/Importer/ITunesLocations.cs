using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

using Flower.Models;

namespace Flower.Importer;

// Music.app's records by the file each one names (its Location), for the two
// importers that read them - ITunesPlayCountImporter and
// ITunesDateAddedImporter, which take a record's value from here before they
// try any tag-derived key.
//
// The file is a stronger identity than the tags: metadata can drift after
// Music.app last indexed a file - confirmed against a real track whose Artist
// tag had been edited ("Takashi Kokubo (小久保隆)" locally vs "Takashi Kokubo"
// in Music.app's stale record) where Location still pointed at the exact same
// file - and a static export's paths can predate an iTunes-to-Music.app
// migration and no longer resolve at all (ITunesPlayCountImporter's class
// comment). Either way a miss here degrades safely: the tag keys get tried
// instead, exactly as if this had never been built.
//
// A song this device holds a file for is looked up by its Path. A song it only
// streams from its server has no Path, but it does have OriginRelativePath -
// where the file sits under the server's music folder - and a server the owner
// filled from Music.app keeps the same file under the same artist and album
// folders: "Beirut/The Rip Tide/01 A Candle's Fire.mp3" is under /music there
// and under ~/Music/Music/Media.localized/Music here. Before that was looked
// up, a streamed song was matched on its tags alone, and in one real library
// 560 songs lost their plays to a tag that disagreed with Music.app's record -
// an album tagged "The Rip Tide 2011" that Music.app calls "The Rip Tide" sat in
// a "Never played" smart playlist after eleven plays a song.
internal sealed class ITunesLocations<T>(Func<T, T, T> combine)
{
    private readonly Dictionary<string, T> _byPath = new(StringComparer.Ordinal);

    // Every path by its file name, so a relative path is compared against the
    // few files that could end with it rather than against the whole library.
    private readonly Dictionary<string, List<string>> _pathsByFileName = new(StringComparer.Ordinal);

    // Two records for the same file - a library that has been through a merge
    // or a duplicate import has those - are combined rather than one replacing
    // the other, since Flower keeps one Track per file.
    public void Add(string? location, T value)
    {
        if (TryDecodeLocationPath(location) is not { } path)
            return;

        if (_byPath.TryGetValue(path, out var existing))
        {
            _byPath[path] = combine(existing, value);
            return;
        }

        _byPath[path] = value;
        var fileName = FileName(path);
        if (!_pathsByFileName.TryGetValue(fileName, out var paths))
            _pathsByFileName[fileName] = paths = new List<string>();
        paths.Add(path);
    }

    // A relative path is only taken when it ends exactly one Music.app file:
    // a shallow one ("01 Intro.mp3" straight under the music folder) can end
    // several, and guessing between them is what the tag keys are for.
    public bool TryFind(Track track, [MaybeNullWhen(false)] out T value)
    {
        if (!string.IsNullOrEmpty(track.Path) && _byPath.TryGetValue(NormalizePath(track.Path), out value))
            return true;

        if (track.OriginRelativePath is { Length: > 0 } relative)
        {
            var suffix = "/" + NormalizePath(relative).Replace('\\', '/');
            string? found = null;
            foreach (var path in _pathsByFileName.GetValueOrDefault(FileName(suffix)) ?? [])
            {
                if (!Slashed(path).EndsWith(suffix, StringComparison.Ordinal))
                    continue;
                if (found != null)
                {
                    found = null;
                    break;
                }
                found = path;
            }

            if (found != null)
            {
                value = _byPath[found];
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? TryDecodeLocationPath(string? location)
    {
        if (string.IsNullOrEmpty(location))
            return null;
        try
        {
            var uri = new Uri(location);
            return uri.IsFile ? NormalizePath(uri.LocalPath) : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    // Confirmed necessary against a real file whose name contains "é": iTunes'
    // Location URL had it as the decomposed form ("e" + a combining acute
    // accent, U+0301) while the same path read off disk elsewhere used the
    // precomposed single-codepoint form (U+00E9) - visually identical, but a
    // byte-for-byte dictionary lookup treated them as two different strings
    // and the path match silently found nothing. Both sides of the match (the
    // paths here and the Track's own, Path or OriginRelativePath) have to go
    // through this same normalization or the mismatch just resurfaces in the
    // opposite direction.
    private static string NormalizePath(string path) => path.Normalize(NormalizationForm.FormC);

    // OriginRelativePath is '/'-separated whatever the server runs on, and a
    // Location decoded on Windows is not.
    private static string Slashed(string path) => path.Replace('\\', '/');

    private static string FileName(string path)
    {
        var slashed = Slashed(path);
        return slashed[(slashed.LastIndexOf('/') + 1)..];
    }
}
