using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Importer;
using Flower.Logging;
using Flower.Models;

namespace Flower.Services;

// Everything Track Info lets a user edit about a song, as one value: the wire
// shape a tag edit travels in (TrackTagEditDto, TrackDto.Tags) and the unit it
// is applied in at the other end. All of it or none - an edit replaces the
// song's tags as a set, which is what the editing device itself did, rather
// than patching fields a slower device might hold older values for.
//
// These are tags in the file's sense. Anything Flower keeps about a track that
// is not written into the file - plays, the star, the playback options - is
// track state and travels separately (TrackStateDto).
public sealed record TrackTagsDto(
    string? Title = null,
    string? Artists = null,
    string? Album = null,
    string? AlbumArtists = null,
    uint TrackNumber = 0,
    uint TrackCount = 0,
    uint DiscNumber = 0,
    uint DiscCount = 0,
    string? Year = null,
    string? Genre = null,
    uint BeatsPerMinute = 0,
    string? InitialKey = null,
    string? Grouping = null,
    string? Composers = null,
    string? Conductor = null,
    string? RemixedBy = null,
    string? Subtitle = null,
    string? Description = null,
    string? Comment = null,
    string? Publisher = null,
    string? Copyright = null,
    string? Isrc = null,
    string? Lyrics = null,
    string? TitleSort = null,
    string? ArtistsSort = null,
    string? AlbumSort = null,
    string? ComposersSort = null,
    bool IsCompilation = false);

// One song's edit, on its way to the server: POST /api/admin/library/tags.
// TrackId is the server's id for the song (Track.OriginTrackId). EditedAt is
// when the edit was made, by the editing device's clock - what decides between
// two devices that edited the same song, and what every other device compares
// against to know the server's tags are newer than its own copy's.
public sealed record TrackTagEditDto(string TrackId, DateTimeOffset EditedAt, TrackTagsDto Tags);

public sealed record LibraryTagEditsRequestDto(List<TrackTagEditDto> Edits);

// NotWritten are the songs whose file the server could not write the tags
// into - a music folder it cannot write to, a file held open. Those edits did
// not happen there and are worth sending again; every other edit is settled,
// applied or not (one the server already has something newer for is settled
// too). LibraryToken is the catalog token the edits produced - see
// LibraryUploadStatusDto.LibraryToken for why it is handed back.
public sealed record LibraryTagEditsResponseDto(int Applied, List<string> NotWritten, string? LibraryToken = null);

// The one mapping between a Track, a TrackTagsDto and a file's tags - which
// used to be written out field by field in each Track Info view, and is now
// also needed by a server applying an edit and a device applying the server's.
public static class TrackTags
{
    public static TrackTagsDto Of(Track track) => new(
        Blank(track.Title), Blank(track.Artists), Blank(track.Album), Blank(track.AlbumArtists),
        track.TrackNumber, track.TrackCount, track.DiscNumber, track.DiscCount,
        Blank(track.Year), Blank(track.Genre), track.BeatsPerMinute, Blank(track.InitialKey), Blank(track.Grouping),
        Blank(track.Composers), Blank(track.Conductor), Blank(track.RemixedBy),
        Blank(track.Subtitle), Blank(track.Description), Blank(track.Comment), Blank(track.Publisher),
        Blank(track.Copyright), Blank(track.ISRC), Blank(track.Lyrics),
        Blank(track.TitleSort), Blank(track.ArtistsSort), Blank(track.AlbumSort), Blank(track.ComposersSort),
        track.IsCompilation);

    public static void ApplyTo(Track track, TrackTagsDto tags)
    {
        track.Title = tags.Title;
        track.Artists = tags.Artists;
        track.Album = tags.Album;
        track.AlbumArtists = tags.AlbumArtists;
        track.TrackNumber = tags.TrackNumber;
        track.TrackCount = tags.TrackCount;
        track.DiscNumber = tags.DiscNumber;
        track.DiscCount = tags.DiscCount;
        track.Year = tags.Year;
        track.Genre = tags.Genre;
        track.BeatsPerMinute = tags.BeatsPerMinute;
        track.InitialKey = tags.InitialKey;
        track.Grouping = tags.Grouping;
        track.Composers = tags.Composers;
        track.Conductor = tags.Conductor;
        track.RemixedBy = tags.RemixedBy;
        track.Subtitle = tags.Subtitle;
        track.Description = tags.Description;
        track.Comment = tags.Comment;
        track.Publisher = tags.Publisher;
        track.Copyright = tags.Copyright;
        track.ISRC = tags.Isrc;
        track.Lyrics = tags.Lyrics;
        track.TitleSort = tags.TitleSort;
        track.ArtistsSort = tags.ArtistsSort;
        track.AlbumSort = tags.AlbumSort;
        track.ComposersSort = tags.ComposersSort;
        track.IsCompilation = tags.IsCompilation;
    }

    // Into the file, through the same TagLib# the importer reads with, so a
    // scan of the file afterwards reads back what was applied. Throws what
    // TagLib throws - a caller decides what a file it cannot write means.
    public static void WriteToFile(string path, TrackTagsDto tags)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        tag.Title = tags.Title;
        tag.Performers = Split(tags.Artists);
        tag.Album = tags.Album;
        tag.AlbumArtists = Split(tags.AlbumArtists);
        tag.Track = tags.TrackNumber;
        tag.TrackCount = tags.TrackCount;
        tag.Disc = tags.DiscNumber;
        tag.DiscCount = tags.DiscCount;
        // Track.Year is the raw string; a tag's year is a number.
        tag.Year = uint.TryParse(tags.Year?.Trim(), out var year) ? year : 0;
        tag.Genres = tags.Genre is { } genre ? [genre] : [];
        tag.BeatsPerMinute = tags.BeatsPerMinute;
        tag.InitialKey = tags.InitialKey;
        tag.Grouping = tags.Grouping;
        tag.Composers = Split(tags.Composers);
        tag.Conductor = tags.Conductor;
        tag.RemixedBy = tags.RemixedBy;
        tag.Subtitle = tags.Subtitle;
        tag.Description = tags.Description;
        tag.Comment = tags.Comment;
        tag.Publisher = tags.Publisher;
        tag.Copyright = tags.Copyright;
        tag.ISRC = tags.Isrc;
        tag.Lyrics = tags.Lyrics;
        tag.TitleSort = tags.TitleSort;
        tag.PerformersSort = Split(tags.ArtistsSort);
        tag.AlbumSort = tags.AlbumSort;
        tag.ComposersSort = Split(tags.ComposersSort);
        CompilationFlag.Write(file, tags.IsCompilation);
        file.Save();
    }

    // A server applying the edits an owner's device sent - the receiving half
    // of POST /api/admin/library/tags, here so the real server and
    // SimulatedFlowerServer answer through the same code.
    //
    // The file first, then the library. A song whose file could not be written
    // keeps its old tags in the library too: the file is what the next scan
    // reads, and a library that disagreed with it would only agree again by
    // losing the edit.
    //
    // The newest edit wins. One the library already has something as new or
    // newer for is dropped without a word - the device that sent it gets the
    // newer tags back on its next pull.
    public static LibraryTagEditsResponseDto ApplyEdits(Library library, IReadOnlyList<TrackTagEditDto> edits, ILogger logger)
    {
        var applied = new List<(Track Track, TrackTagsDto Tags, DateTimeOffset EditedAt, string? FileStamp)>();
        var notWritten = new List<string>();
        foreach (var edit in edits)
        {
            if (library.Find(edit.TrackId) is not { Path: { } path } track)
                continue;
            if (track.TagsEditedAt is { } known && known >= edit.EditedAt)
                continue;

            try
            {
                WriteToFile(path, edit.Tags);
                applied.Add((track, edit.Tags, edit.EditedAt, null));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not write edited tags into {Path}; the edit was not applied", LogPath.Short(path));
                notWritten.Add(edit.TrackId);
            }
        }

        library.ApplySyncedTags(applied);
        return new LibraryTagEditsResponseDto(applied.Count, notWritten, library.ChangeToken);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The way the importer joins them, undone: "A, B" is two performers.
    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
