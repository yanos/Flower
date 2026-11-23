using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// One tag Track Info edits: how a song shows it as text in a box, and how text
// typed there is applied - to the song, and to the TagLib tag of its file. The
// two Track Info screens, the desktop window and the phone's sheet, edit the
// same tags the same way from this one list rather than each spelling out
// two dozen read-and-write pairs of its own.
public sealed class TrackTagField(TrackTagFieldSet set, Func<Track, string> display, Action<Track, TagLib.Tag, string?> apply)
{
    // Which tag this is, for saying what an edit changed.
    public TrackTagFieldSet Set { get; } = set;
    public Func<Track, string> Display { get; } = display;
    public Action<Track, TagLib.Tag, string?> Apply { get; } = apply;
}

public static class TrackTagFields
{
    private static TrackTagField Text(TrackTagFieldSet set, Func<Track, string?> get, Action<Track, string?> setTrack, Action<TagLib.Tag, string?> setTag) =>
        new(set, t => get(t) ?? "", (t, tag, v) =>
        {
            var value = NullIfEmpty(v);
            setTrack(t, value);
            setTag(tag, value);
        });

    // Several names in one box, comma-separated, as the importer joins them -
    // written to the tag as separate values.
    private static TrackTagField Names(TrackTagFieldSet set, Func<Track, string?> get, Action<Track, string?> setTrack, Action<TagLib.Tag, string[]> setTag) =>
        new(set, t => get(t) ?? "", (t, tag, v) =>
        {
            setTrack(t, NullIfEmpty(v));
            setTag(tag, SplitNames(v));
        });

    private static TrackTagField Number(TrackTagFieldSet set, Func<Track, uint> get, Action<Track, uint> setTrack, Action<TagLib.Tag, uint> setTag) =>
        new(set, t => get(t) > 0 ? get(t).ToString() : "", (t, tag, v) =>
        {
            var value = ParseUInt(v);
            setTrack(t, value);
            setTag(tag, value);
        });

    public static readonly TrackTagField Title = Text(TrackTagFieldSet.Title, t => t.Title, (t, v) => t.Title = v, (tag, v) => tag.Title = v);
    public static readonly TrackTagField Artists = Names(TrackTagFieldSet.Artists, t => t.Artists, (t, v) => t.Artists = v, (tag, v) => tag.Performers = v);
    public static readonly TrackTagField Album = Text(TrackTagFieldSet.Album, t => t.Album, (t, v) => t.Album = v, (tag, v) => tag.Album = v);
    public static readonly TrackTagField AlbumArtists = Names(TrackTagFieldSet.AlbumArtists, t => t.AlbumArtists, (t, v) => t.AlbumArtists = v, (tag, v) => tag.AlbumArtists = v);

    public static readonly TrackTagField TrackNumber = Number(TrackTagFieldSet.TrackNumber, t => t.TrackNumber, (t, v) => t.TrackNumber = v, (tag, v) => tag.Track = v);
    public static readonly TrackTagField TrackCount = Number(TrackTagFieldSet.TrackCount, t => t.TrackCount, (t, v) => t.TrackCount = v, (tag, v) => tag.TrackCount = v);
    public static readonly TrackTagField DiscNumber = Number(TrackTagFieldSet.DiscNumber, t => t.DiscNumber, (t, v) => t.DiscNumber = v, (tag, v) => tag.Disc = v);
    public static readonly TrackTagField DiscCount = Number(TrackTagFieldSet.DiscCount, t => t.DiscCount, (t, v) => t.DiscCount = v, (tag, v) => tag.DiscCount = v);

    // Track.Year stays the text typed while the tag's is a number - an
    // asymmetry kept from before this list existed.
    public static readonly TrackTagField Year = new(TrackTagFieldSet.Year, t => t.Year ?? "", (t, tag, v) =>
    {
        t.Year = NullIfEmpty(v);
        tag.Year = ParseUInt(v);
    });

    public static readonly TrackTagField Genre = new(TrackTagFieldSet.Genre, t => t.Genre ?? "", (t, tag, v) =>
    {
        var genre = NullIfEmpty(v);
        t.Genre = genre;
        tag.Genres = genre is { } g ? [g] : [];
    });

    public static readonly TrackTagField BeatsPerMinute = Number(TrackTagFieldSet.BeatsPerMinute, t => t.BeatsPerMinute, (t, v) => t.BeatsPerMinute = v, (tag, v) => tag.BeatsPerMinute = v);
    public static readonly TrackTagField InitialKey = Text(TrackTagFieldSet.InitialKey, t => t.InitialKey, (t, v) => t.InitialKey = v, (tag, v) => tag.InitialKey = v);
    public static readonly TrackTagField Grouping = Text(TrackTagFieldSet.Grouping, t => t.Grouping, (t, v) => t.Grouping = v, (tag, v) => tag.Grouping = v);

    public static readonly TrackTagField Composers = Names(TrackTagFieldSet.Composers, t => t.Composers, (t, v) => t.Composers = v, (tag, v) => tag.Composers = v);
    public static readonly TrackTagField Conductor = Text(TrackTagFieldSet.Conductor, t => t.Conductor, (t, v) => t.Conductor = v, (tag, v) => tag.Conductor = v);
    public static readonly TrackTagField RemixedBy = Text(TrackTagFieldSet.RemixedBy, t => t.RemixedBy, (t, v) => t.RemixedBy = v, (tag, v) => tag.RemixedBy = v);

    public static readonly TrackTagField Subtitle = Text(TrackTagFieldSet.Subtitle, t => t.Subtitle, (t, v) => t.Subtitle = v, (tag, v) => tag.Subtitle = v);
    public static readonly TrackTagField Description = Text(TrackTagFieldSet.Description, t => t.Description, (t, v) => t.Description = v, (tag, v) => tag.Description = v);
    public static readonly TrackTagField Comment = Text(TrackTagFieldSet.Comment, t => t.Comment, (t, v) => t.Comment = v, (tag, v) => tag.Comment = v);
    public static readonly TrackTagField Publisher = Text(TrackTagFieldSet.Publisher, t => t.Publisher, (t, v) => t.Publisher = v, (tag, v) => tag.Publisher = v);
    public static readonly TrackTagField Copyright = Text(TrackTagFieldSet.Copyright, t => t.Copyright, (t, v) => t.Copyright = v, (tag, v) => tag.Copyright = v);
    public static readonly TrackTagField ISRC = Text(TrackTagFieldSet.Isrc, t => t.ISRC, (t, v) => t.ISRC = v, (tag, v) => tag.ISRC = v);
    public static readonly TrackTagField Lyrics = Text(TrackTagFieldSet.Lyrics, t => t.Lyrics, (t, v) => t.Lyrics = v, (tag, v) => tag.Lyrics = v);

    // The sort tags - TSOT/TSOP/TSOA/TSOC and their MP4 and Xiph equivalents,
    // which TagLib exposes on the generic Tag.
    public static readonly TrackTagField TitleSort = Text(TrackTagFieldSet.TitleSort, t => t.TitleSort, (t, v) => t.TitleSort = v, (tag, v) => tag.TitleSort = v);
    public static readonly TrackTagField ArtistsSort = Names(TrackTagFieldSet.ArtistsSort, t => t.ArtistsSort, (t, v) => t.ArtistsSort = v, (tag, v) => tag.PerformersSort = v);
    public static readonly TrackTagField AlbumSort = Text(TrackTagFieldSet.AlbumSort, t => t.AlbumSort, (t, v) => t.AlbumSort = v, (tag, v) => tag.AlbumSort = v);
    public static readonly TrackTagField ComposersSort = Names(TrackTagFieldSet.ComposersSort, t => t.ComposersSort, (t, v) => t.ComposersSort = v, (tag, v) => tag.ComposersSort = v);

    // What a box shows for several songs at once: their one value, or null
    // when they differ - the box is then left empty, saying "Multiple values",
    // and only text typed into it is applied to them all.
    public static string? CommonValue(TrackTagField field, IEnumerable<Track> tracks)
    {
        var values = tracks.Select(field.Display).Distinct().Take(2).ToList();
        return values.Count == 1 ? values[0] : null;
    }

    public static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string[] SplitNames(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? []
            : [.. s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    public static uint ParseUInt(string? s) =>
        uint.TryParse(s?.Trim(), out var n) ? n : 0;
}

// The save half of Track Info, the same on both screens: the edited fields
// applied to every song this device may edit, file first.
public static class TrackTagEdits
{
    // edits are the fields that changed and the text each now holds.
    // perFile is anything else to write while a song's file is open (the
    // desktop's compilation flag, which the generic Tag has no place for), and
    // says whether it took. Returns the songs actually edited - what the
    // caller announces, and so what is stamped as edited and sent to the
    // server.
    //
    // The file is written before the song is touched: a song whose file
    // refused the edit keeps what it had rather than showing an edit its file
    // does not have. A song with no file here - one this device streams from
    // its server - is edited on the song alone, and the edit reaches the
    // server's file from there (LibraryMirrorService). failed, when given,
    // collects the songs whose file refused.
    public static List<Track> Apply(
        IEnumerable<Track> tracks,
        IReadOnlyList<(TrackTagField Field, string Text)> edits,
        Func<Track, bool> mayEdit,
        ILogger logger,
        Func<TagLib.File, Track, bool>? perFile = null,
        Action<Track>? afterFile = null,
        ICollection<Track>? failed = null)
    {
        var edited = new List<Track>();
        foreach (var track in tracks)
        {
            if (!mayEdit(track))
                continue;

            if (track.Path is { } path)
            {
                try
                {
                    var extraTook = WhileNotHeld(() => TagFileSave.Edit(path, file =>
                    {
                        // The fields apply to a song and a tag together; here the
                        // song is a stand-in nobody reads, so only the tag moves.
                        var standIn = new Track();
                        foreach (var (field, text) in edits)
                            field.Apply(standIn, file.Tag, text);
                        return (true, perFile?.Invoke(file, track) ?? true);
                    }));
                    if (extraTook)
                        afterFile?.Invoke(track);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not save tag edits to {Path}; this track's changes were not applied", path);
                    failed?.Add(track);
                    continue;
                }
            }
            else
            {
                afterFile?.Invoke(track);
            }

            var nowhere = new TagLib.Id3v2.Tag();
            foreach (var (field, text) in edits)
                field.Apply(track, nowhere, text);
            edited.Add(track);
        }

        return edited;
    }

    // How long a save waits for a reader to let go of the file.
    private static readonly TimeSpan HeldFileBudget = TimeSpan.FromMilliseconds(500);

    // A cover being read from the same song at the moment of a save
    // (AlbumArtLoader, for the Track Info window itself or a row scrolling by)
    // used to fail it everywhere, when TagLib saved in place under an
    // exclusive lock. TagFileSave replaces the song by a rename now, which no
    // reader blocks on macOS, Linux or the phones; on Windows one still does,
    // since a file open without FileShare.Delete cannot be replaced. Those
    // reads take milliseconds, so the write waits them out; a file held for
    // longer than the budget still fails as it did. Nothing has replaced the
    // song when the lock is refused, so trying again from the start is safe.
    private static T WhileNotHeld<T>(Func<T> write)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return write();
            }
            catch (System.IO.IOException ex) when (IsHeldElsewhere(ex) && clock.Elapsed < HeldFileBudget)
            {
                System.Threading.Thread.Sleep(20);
            }
        }
    }

    // A sharing or lock violation on Windows; on everything else, the
    // EWOULDBLOCK of the flock .NET takes to honour FileShare (35 on Apple's
    // platforms, 11 on Linux and Android).
    private static bool IsHeldElsewhere(System.IO.IOException ex) =>
        ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021)
        || (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ? ex.HResult == 35 : ex.HResult == 11);
}
