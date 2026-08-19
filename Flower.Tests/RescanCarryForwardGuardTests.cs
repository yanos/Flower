using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;

using Flower.Models;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// The guard half of docs/ARCHITECTURE-REVIEW.md Tier 2.6.
//
// A rescan (see Importer) builds brand-new Track instances straight from file
// tags, so anything on a Track that a scan cannot re-derive is lost on every
// launch unless Library.CarryForwardMutableState copies it over. The existing
// LibraryTests pin the current fields one test at a time, which means nothing
// fails when someone adds Starred, Rating or a provider Source tag and forgets
// to list it - and the failure is silent: the field resets to its default on
// the next launch, on every launch, forever.
//
// So this enumerates Track's persisted properties instead of naming them, and
// requires each one to be *either* declared rescannable below *or* observably
// carried forward by a real UpdateTracks. Adding a persisted field to Track and
// nothing else fails this test, with the name of the field in the message.
//
// It is behavioural rather than a source scan: the value is set on a previous
// track, a fresh default-valued track for the same Path goes through the real
// UpdateTracks, and the survivor is inspected. That means it also catches a
// field listed in CarryForwardMutableState but assigned from the wrong side.
public class RescanCarryForwardGuardTests
{
    // Everything the Importer reads back off the file's tags on every scan (see
    // Importer.BuildTrack) plus the two identity/location fields a match is
    // made *on*. These must NOT be carried forward: the whole point of a rescan
    // is that an edited tag takes effect.
    private static readonly HashSet<string> Rescannable = new()
    {
        // Matched on, not carried: a track is found again *by* its Path.
        nameof(Track.Path),

        nameof(Track.Title), nameof(Track.Subtitle), nameof(Track.Artists),
        nameof(Track.AlbumArtists), nameof(Track.IsCompilation),
        nameof(Track.Album), nameof(Track.AlbumSort), nameof(Track.Year),
        nameof(Track.TrackNumber), nameof(Track.TrackCount),
        nameof(Track.DiscNumber), nameof(Track.DiscCount),
        nameof(Track.Composers), nameof(Track.Conductor), nameof(Track.RemixedBy),
        nameof(Track.Genre), nameof(Track.BeatsPerMinute), nameof(Track.InitialKey),
        nameof(Track.Grouping), nameof(Track.Publisher), nameof(Track.ISRC),
        nameof(Track.Comment), nameof(Track.Description), nameof(Track.Copyright),
        nameof(Track.Lyrics),
        nameof(Track.Duration), nameof(Track.Bitrate), nameof(Track.SampleRate),
        nameof(Track.Channels), nameof(Track.BitsPerSample), nameof(Track.Codec),
    };

    public static IEnumerable<object[]> PersistedProperties() =>
        typeof(Track)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
            .Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(PersistedProperties))]
    public void Every_persisted_Track_field_is_either_rescannable_or_carried_forward(string propertyName)
    {
        if (Rescannable.Contains(propertyName))
            return;

        var property = typeof(Track).GetProperty(propertyName)!;

        var previous = new Track { Path = "/music/song.mp3", Title = "Song" };
        var distinctive = Distinctive(property.PropertyType);
        property.SetValue(previous, distinctive);

        var library = new Library(new List<Track> { previous }, NullLogger<Library>.Instance);
        // A fresh scan of the same file: same Path, everything else defaulted.
        library.UpdateTracks(new List<Track> { new() { Path = "/music/song.mp3", Title = "Song" } });

        var survivor = Assert.Single(library.Tracks);
        Assert.True(
            Carried(property.GetValue(survivor), distinctive),
            $"Track.{propertyName} is persisted but a rescan resets it. Either carry it forward in " +
            $"Library.CarryForwardMutableState, or - if the Importer really does re-read it from the " +
            $"file's tags on every scan - add it to {nameof(Rescannable)} in this test.");
    }

    // Value equality, except for the collection-valued fields: RemotePlayCounts
    // is *merged* into the fresh track rather than assigned (see
    // Library.MergeRemotePlayCounts - per-key max, so a relayed report applied
    // twice converges), so the surviving dictionary is a different instance
    // holding the same entries. Carrying it forward is what matters, not which
    // object it arrived in.
    private static bool Carried(object? actual, object expected)
    {
        if (expected is Dictionary<string, int> expectedCounts)
            return actual is Dictionary<string, int> actualCounts &&
                   expectedCounts.All(kv => actualCounts.TryGetValue(kv.Key, out var v) && v == kv.Value);

        return Equals(actual, expected);
    }

    // A value that cannot be confused with the default a freshly-scanned Track
    // arrives with. Deliberately throws on an unhandled type rather than
    // skipping it: a new Track field of some type this doesn't know about must
    // fail loudly here, not silently pass.
    private static object Distinctive(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string))            return "carried-forward";
        if (underlying == typeof(Guid))              return Guid.Parse("11111111-2222-3333-4444-555555555555");
        if (underlying == typeof(int))               return 4711;
        if (underlying == typeof(uint))              return 4711u;
        if (underlying == typeof(bool))              return true;
        if (underlying == typeof(TimeSpan))          return TimeSpan.FromSeconds(4711);
        if (underlying == typeof(DateTimeOffset))    return new DateTimeOffset(1999, 9, 9, 9, 9, 9, TimeSpan.Zero);
        if (underlying == typeof(Dictionary<string, int>)) return new Dictionary<string, int> { ["peer"] = 7 };

        throw new NotSupportedException(
            $"No distinctive value defined for {underlying.Name}. Add one here so the new Track field " +
            $"of this type is actually checked.");
    }
}
