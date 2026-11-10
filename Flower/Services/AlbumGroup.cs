using System;
using System.Collections.Concurrent;

using Flower.Models;

namespace Flower.Services;

// One album as the Albums grid shows it and an album's screen lists it: its
// name, and who it is by. By name alone, Crystal Antlers' "EP" and The Fiery
// Furnaces' "EP" were one album, both bands' songs on one screen under one
// cover.
//
// Who it is by is the album artist (Track.EffectiveAlbumArtist, so a tagged
// compilation is one album), cut to its lead artist, so an album with no
// album artist tag whose songs are by "X" and "X feat. Y" stays one album.
// What the lead artist cannot save is a split release with no album artist
// tag ("Julie Doiron/Okkervil River"), which comes apart into one album per
// band; tagging its album artist puts it back together.
//
// The key is the one string an album is picked by (LibraryBrowserViewModel's
// SelectedSubItems on Albums), carried through navigation as it is.
public static class AlbumGroup
{
    // Between the name and the artist: no album name or artist has it.
    private const char Separator = '\u001F';

    // Distinct album artists are few next to songs, and the lead artist is a
    // regex split - asked once per song on every rebuild of a list.
    private static readonly ConcurrentDictionary<string, string> LeadArtists = new(StringComparer.Ordinal);

    // Null for a song with no album, which no album holds.
    public static string? KeyOf(Track track) =>
        string.IsNullOrEmpty(track.Album)
            ? null
            : track.Album + Separator + LeadArtists.GetOrAdd(
                track.EffectiveAlbumArtist,
                artist => MetadataNames.LeadArtist(artist).ToUpperInvariant());

    // The album's name, as its songs spell it.
    public static string NameOf(string key)
    {
        var at = key.LastIndexOf(Separator);
        return at < 0 ? key : key[..at];
    }
}
