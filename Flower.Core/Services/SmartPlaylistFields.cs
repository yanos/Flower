using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Flower.Models;

namespace Flower.Services;

// The one table describing what a smart playlist can test: for each SmartField,
// its display name, the kind of value it compares against, and how to read it
// off a Track.
//
// Everything reads this - the evaluator, the rule editor's field and operator
// dropdowns, and validation of rules arriving from a peer. Adding a field is a
// single entry here and nothing else, and no consumer gets to hold its own idea
// of what "Genre" means. Same discipline as Schema.cs's album_artist comment:
// one expression, written once, rather than two copies that drift.
public static class SmartPlaylistFields
{
    // Typed accessors rather than one Func<Track, object?>: the evaluator
    // already has to switch on Kind, and this way it reads a double as a double
    // instead of unboxing something it hopes is one.
    public sealed record Descriptor(
        SmartField Field,
        string DisplayName,
        SmartValueKind Kind,
        Func<Track, string?>? Text = null,
        Func<Track, double?>? Number = null,
        Func<Track, TimeSpan?>? Duration = null,
        Func<Track, DateTimeOffset?>? Date = null,
        Func<Track, bool>? Bool = null);

    private static readonly ImmutableArray<Descriptor> Descriptors =
    [
        Text(SmartField.Title,       "Title",        t => t.Title),
        Text(SmartField.Artists,     "Artist",       t => t.Artists),
        Text(SmartField.AlbumArtist, "Album Artist", t => t.EffectiveAlbumArtist),
        Text(SmartField.Album,       "Album",        t => t.Album),
        Text(SmartField.Genre,       "Genre",        t => t.Genre),
        Text(SmartField.Composers,   "Composer",     t => t.Composers),
        Text(SmartField.Grouping,    "Grouping",     t => t.Grouping),
        Text(SmartField.Comment,     "Comment",      t => t.Comment),
        Text(SmartField.Publisher,   "Publisher",    t => t.Publisher),
        Text(SmartField.InitialKey,  "Key",          t => t.InitialKey),
        Text(SmartField.Codec,       "Codec",        t => t.Codec),

        // Year is a tag, and tags are strings: "1979", "1979-03", or something
        // that is not a year at all. A track whose year does not parse has no
        // year to compare, which is what null means to every numeric operator
        // below - it matches nothing, not zero.
        Number(SmartField.Year, "Year", t => int.TryParse(t.Year, out var year) ? year : null),

        Number(SmartField.BeatsPerMinute, "BPM",         t => t.BeatsPerMinute),
        // TotalPlayCount, not PlayCount: plays imported from another library and
        // plays reported by a paired device are still plays, and the number the
        // user sees in the track list is this one.
        Number(SmartField.PlayCount,      "Plays",       t => t.TotalPlayCount),
        Number(SmartField.TrackNumber,    "Track Number", t => t.TrackNumber),
        Number(SmartField.DiscNumber,     "Disc Number", t => t.DiscNumber),
        Number(SmartField.Bitrate,        "Bitrate",     t => t.Bitrate),
        Number(SmartField.SampleRate,     "Sample Rate", t => t.SampleRate),

        new Descriptor(SmartField.Duration, "Duration", SmartValueKind.Duration, Duration: t => t.Duration),

        Date(SmartField.DateAdded,    "Date Added",   t => t.DateAdded),
        Date(SmartField.LastPlayedAt, "Last Played",  t => t.LastPlayedAt),
        Date(SmartField.StarredAt,    "Date Starred", t => t.StarredAt),

        Bool(SmartField.Starred,             "Starred",              t => t.Starred),
        Bool(SmartField.IsCompilation,       "Compilation",          t => t.IsCompilation),
        Bool(SmartField.IsLocallyDownloaded, "Downloaded",           t => t.IsLocallyDownloaded),
        Bool(SmartField.IgnoreWhenShuffling, "Skip When Shuffling",  t => t.IgnoreWhenShuffling),

        // No accessor: membership is a property of a playlist, not of a track,
        // so the evaluator resolves this one against its context instead.
        new Descriptor(SmartField.Playlist, "Playlist", SmartValueKind.Playlist),
    ];

    private static readonly ImmutableDictionary<SmartField, Descriptor> ByField =
        Descriptors.ToImmutableDictionary(d => d.Field);

    public static ImmutableArray<Descriptor> All => Descriptors;

    public static Descriptor For(SmartField field) =>
        ByField.TryGetValue(field, out var descriptor)
            ? descriptor
            // Reachable from a rules blob written by a newer Flower and synced
            // to this one. Loud, because silently dropping the condition would
            // widen the playlist rather than narrow it.
            : throw new ArgumentOutOfRangeException(nameof(field), field, "No descriptor for this smart playlist field.");

    public static SmartValueKind KindOf(SmartField field) => For(field).Kind;

    // Which operators the editor should offer for a field, and which the
    // evaluator will accept. Ordered as they should appear in a dropdown.
    public static ImmutableArray<SmartOperator> OperatorsFor(SmartValueKind kind) => kind switch
    {
        SmartValueKind.Text =>
        [
            SmartOperator.Is, SmartOperator.IsNot,
            SmartOperator.Contains, SmartOperator.DoesNotContain,
            SmartOperator.StartsWith, SmartOperator.EndsWith,
            SmartOperator.IsEmpty, SmartOperator.IsNotEmpty,
        ],
        SmartValueKind.Number or SmartValueKind.Duration =>
        [
            SmartOperator.Is, SmartOperator.IsNot,
            SmartOperator.GreaterThan, SmartOperator.LessThan, SmartOperator.Between,
        ],
        SmartValueKind.Date =>
        [
            SmartOperator.Is, SmartOperator.IsNot,
            SmartOperator.GreaterThan, SmartOperator.LessThan, SmartOperator.Between,
            SmartOperator.InTheLast, SmartOperator.NotInTheLast,
            SmartOperator.IsEmpty, SmartOperator.IsNotEmpty,
        ],
        SmartValueKind.Bool or SmartValueKind.Playlist =>
        [
            SmartOperator.Is, SmartOperator.IsNot,
        ],
        _ => [],
    };

    public static ImmutableArray<SmartOperator> OperatorsFor(SmartField field) => OperatorsFor(KindOf(field));

    public static bool Supports(SmartField field, SmartOperator op) => OperatorsFor(field).Contains(op);

    private static Descriptor Text(SmartField field, string name, Func<Track, string?> accessor) =>
        new(field, name, SmartValueKind.Text, Text: accessor);

    private static Descriptor Number(SmartField field, string name, Func<Track, double?> accessor) =>
        new(field, name, SmartValueKind.Number, Number: accessor);

    private static Descriptor Date(SmartField field, string name, Func<Track, DateTimeOffset?> accessor) =>
        new(field, name, SmartValueKind.Date, Date: accessor);

    private static Descriptor Bool(SmartField field, string name, Func<Track, bool> accessor) =>
        new(field, name, SmartValueKind.Bool, Bool: accessor);
}
