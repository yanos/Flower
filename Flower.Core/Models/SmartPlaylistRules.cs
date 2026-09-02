using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Flower.Models;

// The definition of a smart playlist: the query a playlist is, rather than the
// songs it currently holds. See docs/SMART-PLAYLIST-PLAN.md.
//
// This record - not the resulting track list - is the state. A smart playlist's
// Tracks are a materialized cache of evaluating these rules against whatever
// library the evaluating device happens to hold, which is why two devices with
// different subsets of the music legitimately show different contents for the
// same playlist, and why sync ships the rules rather than the members.
public sealed record SmartPlaylistRules(
    MatchMode Mode,
    IReadOnlyList<SmartCondition> Conditions,
    SmartLimit? Limit = null,
    // "Live updating" in iTunes' vocabulary: false means evaluate once, when
    // saved, and then leave the contents alone until the rules change again.
    bool LiveUpdating = true)
{
    public static SmartPlaylistRules MatchAll(params SmartCondition[] conditions) =>
        new(MatchMode.All, conditions);

    public static SmartPlaylistRules MatchAny(params SmartCondition[] conditions) =>
        new(MatchMode.Any, conditions);
}

public enum MatchMode
{
    All,
    Any,
}

public sealed record SmartCondition(SmartField Field, SmartOperator Operator, SmartValue Value);

// Every field a condition can test, and the only place new ones get added -
// SmartPlaylistFields maps each to its display name, value kind and accessor.
//
// Explicitly numbered because these are persisted (Schema V6's playlists.rules)
// and travel over the sync wire, so the numbers are the contract even while the
// names are not. Grouped in blocks of ten by value kind, leaving room to add a
// field beside its relatives instead of at the end.
public enum SmartField
{
    Title          = 1,
    Artists        = 2,
    AlbumArtist    = 3,
    Album          = 4,
    Genre          = 5,
    Composers      = 6,
    Grouping       = 7,
    Comment        = 8,
    Publisher      = 9,
    InitialKey     = 10,
    Codec          = 11,

    Year           = 20,
    BeatsPerMinute = 21,
    PlayCount      = 22,
    TrackNumber    = 23,
    DiscNumber     = 24,
    Bitrate        = 25,
    SampleRate     = 26,

    Duration       = 40,

    DateAdded      = 50,
    LastPlayedAt   = 51,
    StarredAt      = 52,

    Starred             = 70,
    IsCompilation       = 71,
    IsLocallyDownloaded = 72,
    IgnoreWhenShuffling = 73,

    // The one field that is not a property of a track at all: "is / is not in
    // playlist X". What makes smart playlists compose, and the only source of
    // dependencies between them - see SmartPlaylistGraph.
    Playlist       = 90,
}

public enum SmartValueKind
{
    Text,
    Number,
    Duration,
    Date,
    Bool,
    Playlist,
}

// Numbered for the same reason SmartField is.
public enum SmartOperator
{
    Is             = 1,
    IsNot          = 2,

    Contains       = 10,
    DoesNotContain = 11,
    StartsWith     = 12,
    EndsWith       = 13,

    GreaterThan    = 20,
    LessThan       = 21,
    Between        = 22,

    // Date-only, and the reason SmartValue.Relative exists: "in the last 30
    // days" has to stay relative, resolved against a clock at evaluation time.
    InTheLast      = 30,
    NotInTheLast   = 31,

    IsEmpty        = 40,
    IsNotEmpty     = 41,
}

// The right-hand side of a condition. One case per value kind, plus the two
// that are not literals: Relative (a date offset, never collapsed to an
// instant) and Range (the pair Between needs).
//
// The discriminators are persisted (Schema V6's playlists.rules) and travel
// over the sync wire, so like SmartField's numbers they are the contract - a
// case can be renamed in C#, its tag cannot. Short strings rather than the
// default assembly-qualified name, which would bake the namespace of a private
// nested type into every stored rule.
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Text), "text")]
[JsonDerivedType(typeof(Number), "number")]
[JsonDerivedType(typeof(Duration), "duration")]
[JsonDerivedType(typeof(Date), "date")]
[JsonDerivedType(typeof(Relative), "relative")]
[JsonDerivedType(typeof(Bool), "bool")]
[JsonDerivedType(typeof(PlaylistRef), "playlist")]
[JsonDerivedType(typeof(Range), "range")]
[JsonDerivedType(typeof(None), "none")]
public abstract record SmartValue
{
    public sealed record Text(string Value) : SmartValue;

    public sealed record Number(double Value) : SmartValue;

    // Ticks on the wire, matching TimeSpanTicksConverter's choice everywhere
    // else in this codebase, rather than System.Text.Json's own "hh:mm:ss"
    // string - the same value in one representation, not two.
    public sealed record Duration(
        [property: JsonConverter(typeof(TimeSpanTicksConverter))] TimeSpan Value) : SmartValue;

    public sealed record Date(DateTimeOffset Value) : SmartValue;

    // "the last 30 days", stored as 30 + Days. Storing the instant this
    // resolves to today would freeze the window: the playlist would go on
    // matching the same four weeks forever, and nothing would look wrong until
    // someone noticed it had not changed in a year.
    public sealed record Relative(int Amount, RelativeUnit Unit) : SmartValue;

    public sealed record Bool(bool Value) : SmartValue;

    public sealed record PlaylistRef(Guid PlaylistId) : SmartValue;

    public sealed record Range(SmartValue From, SmartValue To) : SmartValue;

    // Nothing on the right-hand side at all - IsEmpty/IsNotEmpty. A singleton
    // rather than null so a condition never has to carry a nullable value.
    // Round-tripping it produces a fresh instance rather than Instance, which
    // is why every comparison of one is by value (record equality) and never
    // ReferenceEquals.
    public sealed record None : SmartValue
    {
        public static readonly None Instance = new();
    }
}

public enum RelativeUnit
{
    Minutes,
    Hours,
    Days,
    Weeks,
    Months,
    Years,
}

// iTunes' "Limit to N items selected by X". Applied after the conditions have
// matched, and it is what makes "25 songs I have not heard in longest" a
// playlist rather than a report.
public sealed record SmartLimit(int Amount, LimitUnit Unit, LimitSelector SelectedBy);

public enum LimitUnit
{
    Items,
    Minutes,
    Hours,
}

public enum LimitSelector
{
    Random,
    Title,
    Artist,
    Album,
    MostPlayed,
    LeastPlayed,
    MostRecentlyPlayed,
    LeastRecentlyPlayed,
    MostRecentlyAdded,
    LeastRecentlyAdded,
}
