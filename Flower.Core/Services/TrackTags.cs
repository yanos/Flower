using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Importer;
using Flower.Logging;
using Flower.Models;

namespace Flower.Services;

// Everything Track Info lets a user edit about a song, as one value: the wire
// shape a tag edit travels in (TrackTagEditDto, TrackDto.Tags). What is applied
// at the other end is only the fields named as edited (TrackTagFieldSet). The
// rest of the value cannot be trusted to be the song's: a device streaming a
// song nobody has edited was never told its lyrics, composer or comment, and
// Genre holds only the first of a file's genres.
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

// Which of TrackTagsDto's fields an edit changed.
[Flags]
public enum TrackTagFieldSet : uint
{
    None = 0,
    Title = 1u << 0,
    Artists = 1u << 1,
    Album = 1u << 2,
    AlbumArtists = 1u << 3,
    TrackNumber = 1u << 4,
    TrackCount = 1u << 5,
    DiscNumber = 1u << 6,
    DiscCount = 1u << 7,
    Year = 1u << 8,
    Genre = 1u << 9,
    BeatsPerMinute = 1u << 10,
    InitialKey = 1u << 11,
    Grouping = 1u << 12,
    Composers = 1u << 13,
    Conductor = 1u << 14,
    RemixedBy = 1u << 15,
    Subtitle = 1u << 16,
    Description = 1u << 17,
    Comment = 1u << 18,
    Publisher = 1u << 19,
    Copyright = 1u << 20,
    Isrc = 1u << 21,
    Lyrics = 1u << 22,
    TitleSort = 1u << 23,
    ArtistsSort = 1u << 24,
    AlbumSort = 1u << 25,
    ComposersSort = 1u << 26,
    IsCompilation = 1u << 27,
    All = (1u << 28) - 1,
}

// One song's edit, on its way to the server: POST /api/admin/library/tags.
// TrackId is the server's id for the song (Track.OriginTrackId). EditedAt is
// when the edit was made, by the editing device's clock - what decides between
// two devices that edited the same song, and what every other device compares
// against to know the server's tags are newer than its own copy's. Fields are
// the tags edited; nothing else in Tags is applied.
public sealed record TrackTagEditDto(string TrackId, DateTimeOffset EditedAt, TrackTagsDto Tags, TrackTagFieldSet Fields);

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

    public static void ApplyTo(Track track, TrackTagsDto tags) => ApplyTo(track, tags, TrackTagFieldSet.All);

    // Only the named fields: a song whose Title alone was edited keeps every
    // other value exactly as it was, untrimmed, so it still matches its file.
    public static void ApplyTo(Track track, TrackTagsDto tags, TrackTagFieldSet fields)
    {
        if (fields.HasFlag(TrackTagFieldSet.Title))
            track.Title = tags.Title;
        if (fields.HasFlag(TrackTagFieldSet.Artists))
            track.Artists = tags.Artists;
        if (fields.HasFlag(TrackTagFieldSet.Album))
            track.Album = tags.Album;
        if (fields.HasFlag(TrackTagFieldSet.AlbumArtists))
            track.AlbumArtists = tags.AlbumArtists;
        if (fields.HasFlag(TrackTagFieldSet.TrackNumber))
            track.TrackNumber = tags.TrackNumber;
        if (fields.HasFlag(TrackTagFieldSet.TrackCount))
            track.TrackCount = tags.TrackCount;
        if (fields.HasFlag(TrackTagFieldSet.DiscNumber))
            track.DiscNumber = tags.DiscNumber;
        if (fields.HasFlag(TrackTagFieldSet.DiscCount))
            track.DiscCount = tags.DiscCount;
        if (fields.HasFlag(TrackTagFieldSet.Year))
            track.Year = tags.Year;
        if (fields.HasFlag(TrackTagFieldSet.Genre))
            track.Genre = tags.Genre;
        if (fields.HasFlag(TrackTagFieldSet.BeatsPerMinute))
            track.BeatsPerMinute = tags.BeatsPerMinute;
        if (fields.HasFlag(TrackTagFieldSet.InitialKey))
            track.InitialKey = tags.InitialKey;
        if (fields.HasFlag(TrackTagFieldSet.Grouping))
            track.Grouping = tags.Grouping;
        if (fields.HasFlag(TrackTagFieldSet.Composers))
            track.Composers = tags.Composers;
        if (fields.HasFlag(TrackTagFieldSet.Conductor))
            track.Conductor = tags.Conductor;
        if (fields.HasFlag(TrackTagFieldSet.RemixedBy))
            track.RemixedBy = tags.RemixedBy;
        if (fields.HasFlag(TrackTagFieldSet.Subtitle))
            track.Subtitle = tags.Subtitle;
        if (fields.HasFlag(TrackTagFieldSet.Description))
            track.Description = tags.Description;
        if (fields.HasFlag(TrackTagFieldSet.Comment))
            track.Comment = tags.Comment;
        if (fields.HasFlag(TrackTagFieldSet.Publisher))
            track.Publisher = tags.Publisher;
        if (fields.HasFlag(TrackTagFieldSet.Copyright))
            track.Copyright = tags.Copyright;
        if (fields.HasFlag(TrackTagFieldSet.Isrc))
            track.ISRC = tags.Isrc;
        if (fields.HasFlag(TrackTagFieldSet.Lyrics))
            track.Lyrics = tags.Lyrics;
        if (fields.HasFlag(TrackTagFieldSet.TitleSort))
            track.TitleSort = tags.TitleSort;
        if (fields.HasFlag(TrackTagFieldSet.ArtistsSort))
            track.ArtistsSort = tags.ArtistsSort;
        if (fields.HasFlag(TrackTagFieldSet.AlbumSort))
            track.AlbumSort = tags.AlbumSort;
        if (fields.HasFlag(TrackTagFieldSet.ComposersSort))
            track.ComposersSort = tags.ComposersSort;
        if (fields.HasFlag(TrackTagFieldSet.IsCompilation))
            track.IsCompilation = tags.IsCompilation;
    }

    public static uint ParseUnsigned(string? text) => uint.TryParse(text?.Trim(), out var value) ? value : 0;

    // Which fields of the two differ.
    public static TrackTagFieldSet Differences(TrackTagsDto a, TrackTagsDto b) =>
        TrackTagFieldSet.None
        | (a.Title != b.Title ? TrackTagFieldSet.Title : TrackTagFieldSet.None)
        | (a.Artists != b.Artists ? TrackTagFieldSet.Artists : TrackTagFieldSet.None)
        | (a.Album != b.Album ? TrackTagFieldSet.Album : TrackTagFieldSet.None)
        | (a.AlbumArtists != b.AlbumArtists ? TrackTagFieldSet.AlbumArtists : TrackTagFieldSet.None)
        | (a.TrackNumber != b.TrackNumber ? TrackTagFieldSet.TrackNumber : TrackTagFieldSet.None)
        | (a.TrackCount != b.TrackCount ? TrackTagFieldSet.TrackCount : TrackTagFieldSet.None)
        | (a.DiscNumber != b.DiscNumber ? TrackTagFieldSet.DiscNumber : TrackTagFieldSet.None)
        | (a.DiscCount != b.DiscCount ? TrackTagFieldSet.DiscCount : TrackTagFieldSet.None)
        | (a.Year != b.Year ? TrackTagFieldSet.Year : TrackTagFieldSet.None)
        | (a.Genre != b.Genre ? TrackTagFieldSet.Genre : TrackTagFieldSet.None)
        | (a.BeatsPerMinute != b.BeatsPerMinute ? TrackTagFieldSet.BeatsPerMinute : TrackTagFieldSet.None)
        | (a.InitialKey != b.InitialKey ? TrackTagFieldSet.InitialKey : TrackTagFieldSet.None)
        | (a.Grouping != b.Grouping ? TrackTagFieldSet.Grouping : TrackTagFieldSet.None)
        | (a.Composers != b.Composers ? TrackTagFieldSet.Composers : TrackTagFieldSet.None)
        | (a.Conductor != b.Conductor ? TrackTagFieldSet.Conductor : TrackTagFieldSet.None)
        | (a.RemixedBy != b.RemixedBy ? TrackTagFieldSet.RemixedBy : TrackTagFieldSet.None)
        | (a.Subtitle != b.Subtitle ? TrackTagFieldSet.Subtitle : TrackTagFieldSet.None)
        | (a.Description != b.Description ? TrackTagFieldSet.Description : TrackTagFieldSet.None)
        | (a.Comment != b.Comment ? TrackTagFieldSet.Comment : TrackTagFieldSet.None)
        | (a.Publisher != b.Publisher ? TrackTagFieldSet.Publisher : TrackTagFieldSet.None)
        | (a.Copyright != b.Copyright ? TrackTagFieldSet.Copyright : TrackTagFieldSet.None)
        | (a.Isrc != b.Isrc ? TrackTagFieldSet.Isrc : TrackTagFieldSet.None)
        | (a.Lyrics != b.Lyrics ? TrackTagFieldSet.Lyrics : TrackTagFieldSet.None)
        | (a.TitleSort != b.TitleSort ? TrackTagFieldSet.TitleSort : TrackTagFieldSet.None)
        | (a.ArtistsSort != b.ArtistsSort ? TrackTagFieldSet.ArtistsSort : TrackTagFieldSet.None)
        | (a.AlbumSort != b.AlbumSort ? TrackTagFieldSet.AlbumSort : TrackTagFieldSet.None)
        | (a.ComposersSort != b.ComposersSort ? TrackTagFieldSet.ComposersSort : TrackTagFieldSet.None)
        | (a.IsCompilation != b.IsCompilation ? TrackTagFieldSet.IsCompilation : TrackTagFieldSet.None);

    // Into the file, through the same TagLib# the importer reads with, so a
    // scan of the file afterwards reads back what was applied. Only the named
    // fields: whatever else the file holds (a second genre, a tag Flower does
    // not read) stays as it is. Throws what TagLib throws - a caller decides
    // what a file it cannot write means.
    public static void WriteToFile(string path, TrackTagsDto tags, TrackTagFieldSet fields = TrackTagFieldSet.All) =>
        TagFileSave.Edit(path, file => Write(file, tags, fields));

    public static void Write(TagLib.File file, TrackTagsDto tags, TrackTagFieldSet fields)
    {
        var tag = file.Tag;
        if (fields.HasFlag(TrackTagFieldSet.Title))
            tag.Title = tags.Title;
        if (fields.HasFlag(TrackTagFieldSet.Artists))
            tag.Performers = Split(tags.Artists);
        if (fields.HasFlag(TrackTagFieldSet.Album))
            tag.Album = tags.Album;
        if (fields.HasFlag(TrackTagFieldSet.AlbumArtists))
            tag.AlbumArtists = Split(tags.AlbumArtists);
        if (fields.HasFlag(TrackTagFieldSet.TrackNumber))
            tag.Track = tags.TrackNumber;
        if (fields.HasFlag(TrackTagFieldSet.TrackCount))
            tag.TrackCount = tags.TrackCount;
        if (fields.HasFlag(TrackTagFieldSet.DiscNumber))
            tag.Disc = tags.DiscNumber;
        if (fields.HasFlag(TrackTagFieldSet.DiscCount))
            tag.DiscCount = tags.DiscCount;
        // Track.Year is the raw string; a tag's year is a number.
        if (fields.HasFlag(TrackTagFieldSet.Year))
            tag.Year = ParseUnsigned(tags.Year);
        if (fields.HasFlag(TrackTagFieldSet.Genre))
            tag.Genres = tags.Genre is { } genre ? [genre] : [];
        if (fields.HasFlag(TrackTagFieldSet.BeatsPerMinute))
            tag.BeatsPerMinute = tags.BeatsPerMinute;
        if (fields.HasFlag(TrackTagFieldSet.InitialKey))
            tag.InitialKey = tags.InitialKey;
        if (fields.HasFlag(TrackTagFieldSet.Grouping))
            tag.Grouping = tags.Grouping;
        if (fields.HasFlag(TrackTagFieldSet.Composers))
            tag.Composers = Split(tags.Composers);
        if (fields.HasFlag(TrackTagFieldSet.Conductor))
            tag.Conductor = tags.Conductor;
        if (fields.HasFlag(TrackTagFieldSet.RemixedBy))
            tag.RemixedBy = tags.RemixedBy;
        if (fields.HasFlag(TrackTagFieldSet.Subtitle))
            tag.Subtitle = tags.Subtitle;
        if (fields.HasFlag(TrackTagFieldSet.Description))
            tag.Description = tags.Description;
        if (fields.HasFlag(TrackTagFieldSet.Comment))
            tag.Comment = tags.Comment;
        if (fields.HasFlag(TrackTagFieldSet.Publisher))
            tag.Publisher = tags.Publisher;
        if (fields.HasFlag(TrackTagFieldSet.Copyright))
            tag.Copyright = tags.Copyright;
        if (fields.HasFlag(TrackTagFieldSet.Isrc))
            tag.ISRC = tags.Isrc;
        if (fields.HasFlag(TrackTagFieldSet.Lyrics))
            tag.Lyrics = tags.Lyrics;
        if (fields.HasFlag(TrackTagFieldSet.TitleSort))
            tag.TitleSort = tags.TitleSort;
        if (fields.HasFlag(TrackTagFieldSet.ArtistsSort))
            tag.PerformersSort = Split(tags.ArtistsSort);
        if (fields.HasFlag(TrackTagFieldSet.AlbumSort))
            tag.AlbumSort = tags.AlbumSort;
        if (fields.HasFlag(TrackTagFieldSet.ComposersSort))
            tag.ComposersSort = Split(tags.ComposersSort);
        if (fields.HasFlag(TrackTagFieldSet.IsCompilation))
            CompilationFlag.Write(file, tags.IsCompilation);
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
        var applied = new List<(Track Track, TrackTagsDto Tags, TrackTagFieldSet Fields, DateTimeOffset EditedAt, string? FileStamp, TrackTagFieldSet EditedFields)>();
        var notWritten = new List<string>();
        foreach (var edit in edits)
        {
            if (library.Find(edit.TrackId) is not { Path: { } path } track)
                continue;
            if (track.TagsEditedAt is { } known && known >= edit.EditedAt)
                continue;
            if (edit.Fields == TrackTagFieldSet.None)
                continue;

            try
            {
                WriteToFile(path, edit.Tags, edit.Fields);
                applied.Add((track, edit.Tags, edit.Fields, edit.EditedAt, null, track.TagFieldsEdited | edit.Fields));
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

    public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The way the importer joins them, undone: "A, B" is two performers.
    private static string[] Split(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
