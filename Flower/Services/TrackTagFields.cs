using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// Track Info supplies text; parsing produces a masked draft shared by the
// local save and sync paths. Field application belongs to TrackTags in Core.
public sealed class TrackTagField(
    TrackTagFieldSet set, Func<Track, string> display, Func<TrackTagsDto, string?, TrackTagsDto> edit)
{
    public TrackTagFieldSet Set { get; } = set;
    public Func<Track, string> Display { get; } = display;
    public Func<TrackTagsDto, string?, TrackTagsDto> Edit { get; } = edit;
}

public static class TrackTagFields
{
    public static readonly TrackTagField Title = new(TrackTagFieldSet.Title, t => t.Title ?? "", (draft, v) => draft with { Title = TrackTags.Blank(v) });
    public static readonly TrackTagField Artists = new(TrackTagFieldSet.Artists, t => t.Artists ?? "", (draft, v) => draft with { Artists = TrackTags.Blank(v) });
    public static readonly TrackTagField Album = new(TrackTagFieldSet.Album, t => t.Album ?? "", (draft, v) => draft with { Album = TrackTags.Blank(v) });
    public static readonly TrackTagField AlbumArtists = new(TrackTagFieldSet.AlbumArtists, t => t.AlbumArtists ?? "", (draft, v) => draft with { AlbumArtists = TrackTags.Blank(v) });
    public static readonly TrackTagField TrackNumber = new(TrackTagFieldSet.TrackNumber, t => t.TrackNumber > 0 ? t.TrackNumber.ToString() : "", (draft, v) => draft with { TrackNumber = TrackTags.ParseUnsigned(v) });
    public static readonly TrackTagField TrackCount = new(TrackTagFieldSet.TrackCount, t => t.TrackCount > 0 ? t.TrackCount.ToString() : "", (draft, v) => draft with { TrackCount = TrackTags.ParseUnsigned(v) });
    public static readonly TrackTagField DiscNumber = new(TrackTagFieldSet.DiscNumber, t => t.DiscNumber > 0 ? t.DiscNumber.ToString() : "", (draft, v) => draft with { DiscNumber = TrackTags.ParseUnsigned(v) });
    public static readonly TrackTagField DiscCount = new(TrackTagFieldSet.DiscCount, t => t.DiscCount > 0 ? t.DiscCount.ToString() : "", (draft, v) => draft with { DiscCount = TrackTags.ParseUnsigned(v) });
    public static readonly TrackTagField Year = new(TrackTagFieldSet.Year, t => t.Year ?? "", (draft, v) => draft with { Year = TrackTags.Blank(v) });
    public static readonly TrackTagField Genre = new(TrackTagFieldSet.Genre, t => t.Genre ?? "", (draft, v) => draft with { Genre = TrackTags.Blank(v) });
    public static readonly TrackTagField BeatsPerMinute = new(TrackTagFieldSet.BeatsPerMinute, t => t.BeatsPerMinute > 0 ? t.BeatsPerMinute.ToString() : "", (draft, v) => draft with { BeatsPerMinute = TrackTags.ParseUnsigned(v) });
    public static readonly TrackTagField InitialKey = new(TrackTagFieldSet.InitialKey, t => t.InitialKey ?? "", (draft, v) => draft with { InitialKey = TrackTags.Blank(v) });
    public static readonly TrackTagField Grouping = new(TrackTagFieldSet.Grouping, t => t.Grouping ?? "", (draft, v) => draft with { Grouping = TrackTags.Blank(v) });
    public static readonly TrackTagField Composers = new(TrackTagFieldSet.Composers, t => t.Composers ?? "", (draft, v) => draft with { Composers = TrackTags.Blank(v) });
    public static readonly TrackTagField Conductor = new(TrackTagFieldSet.Conductor, t => t.Conductor ?? "", (draft, v) => draft with { Conductor = TrackTags.Blank(v) });
    public static readonly TrackTagField RemixedBy = new(TrackTagFieldSet.RemixedBy, t => t.RemixedBy ?? "", (draft, v) => draft with { RemixedBy = TrackTags.Blank(v) });
    public static readonly TrackTagField Subtitle = new(TrackTagFieldSet.Subtitle, t => t.Subtitle ?? "", (draft, v) => draft with { Subtitle = TrackTags.Blank(v) });
    public static readonly TrackTagField Description = new(TrackTagFieldSet.Description, t => t.Description ?? "", (draft, v) => draft with { Description = TrackTags.Blank(v) });
    public static readonly TrackTagField Comment = new(TrackTagFieldSet.Comment, t => t.Comment ?? "", (draft, v) => draft with { Comment = TrackTags.Blank(v) });
    public static readonly TrackTagField Publisher = new(TrackTagFieldSet.Publisher, t => t.Publisher ?? "", (draft, v) => draft with { Publisher = TrackTags.Blank(v) });
    public static readonly TrackTagField Copyright = new(TrackTagFieldSet.Copyright, t => t.Copyright ?? "", (draft, v) => draft with { Copyright = TrackTags.Blank(v) });
    public static readonly TrackTagField ISRC = new(TrackTagFieldSet.Isrc, t => t.ISRC ?? "", (draft, v) => draft with { Isrc = TrackTags.Blank(v) });
    public static readonly TrackTagField Lyrics = new(TrackTagFieldSet.Lyrics, t => t.Lyrics ?? "", (draft, v) => draft with { Lyrics = TrackTags.Blank(v) });
    public static readonly TrackTagField TitleSort = new(TrackTagFieldSet.TitleSort, t => t.TitleSort ?? "", (draft, v) => draft with { TitleSort = TrackTags.Blank(v) });
    public static readonly TrackTagField ArtistsSort = new(TrackTagFieldSet.ArtistsSort, t => t.ArtistsSort ?? "", (draft, v) => draft with { ArtistsSort = TrackTags.Blank(v) });
    public static readonly TrackTagField AlbumSort = new(TrackTagFieldSet.AlbumSort, t => t.AlbumSort ?? "", (draft, v) => draft with { AlbumSort = TrackTags.Blank(v) });
    public static readonly TrackTagField ComposersSort = new(TrackTagFieldSet.ComposersSort, t => t.ComposersSort ?? "", (draft, v) => draft with { ComposersSort = TrackTags.Blank(v) });

    // What a box shows for several songs at once: their one value, or null
    // when they differ - the box is then left empty, saying "Multiple values",
    // and only text typed into it is applied to them all.
    public static string? CommonValue(TrackTagField field, IEnumerable<Track> tracks)
    {
        var values = tracks.Select(field.Display).Distinct().Take(2).ToList();
        return values.Count == 1 ? values[0] : null;
    }

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
        var draft = new TrackTagsDto();
        var fields = TrackTagFieldSet.None;
        foreach (var (field, text) in edits)
        {
            draft = field.Edit(draft, text);
            fields |= field.Set;
        }

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
                        TrackTags.Write(file, draft, fields);
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

            TrackTags.ApplyTo(track, draft, fields);
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
