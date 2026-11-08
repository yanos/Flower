using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;

namespace Flower.Tests.MusicLibrary;

public class TrackTests
{
    [Fact]
    public void TotalPlayCount_sums_own_imported_and_every_remote_devices_count()
    {
        var track = new Track
        {
            PlayCount = 3,
            ImportedPlayCount = 5,
            RemotePlayCounts = new Dictionary<string, int> { ["peer-1"] = 2, ["peer-2"] = 7 },
        };

        Assert.Equal(17, track.TotalPlayCount);
    }

    [Fact]
    public void EffectiveAlbumArtist_prefers_AlbumArtists_when_present()
    {
        var track = new Track { Artists = "Track Artist", AlbumArtists = "The Album Artist" };

        Assert.Equal("The Album Artist", track.EffectiveAlbumArtist);
    }

    [Fact]
    public void EffectiveAlbumArtist_falls_back_to_Various_Artists_for_a_compilation_with_no_AlbumArtists_tag()
    {
        var track = new Track { Artists = "Track Artist", AlbumArtists = "", IsCompilation = true };

        Assert.Equal("Various Artists", track.EffectiveAlbumArtist);
    }

    [Fact]
    public void EffectiveAlbumArtist_falls_back_to_Artists_for_an_ordinary_album_with_no_AlbumArtists_tag()
    {
        var track = new Track { Artists = "Track Artist", AlbumArtists = null, IsCompilation = false };

        Assert.Equal("Track Artist", track.EffectiveAlbumArtist);
    }

    // HasSameStateAs is a hand-written list of fields, and a field it misses
    // is a rescan that changed it going unannounced. So: every settable
    // property, changed alone, must make two otherwise identical tracks
    // differ.
    public static TheoryData<string> SettableProperties()
    {
        var data = new TheoryData<string>();
        foreach (var property in typeof(Track).GetProperties().Where(p => p.CanWrite && p.SetMethod!.IsPublic))
            data.Add(property.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SettableProperties))]
    public void HasSameStateAs_notices_a_change_to_any_one_field(string name)
    {
        var track = new Track { Title = "Pirsomnia", Path = "/music/a.flac" };
        var changed = track.Clone();
        var property = typeof(Track).GetProperty(name)!;

        property.SetValue(changed, Different(property.PropertyType, property.GetValue(track)));

        Assert.True(track.HasSameStateAs(track.Clone()));
        Assert.False(track.HasSameStateAs(changed));
    }

    private static object Different(Type type, object? value) => type switch
    {
        _ when type == typeof(string) => (value as string) + "x",
        _ when type == typeof(bool) => !(bool)value!,
        _ when type == typeof(int) => (int)value! + 1,
        _ when type == typeof(uint) => (uint)value! + 1,
        _ when type == typeof(Guid) => Guid.NewGuid(),
        _ when type == typeof(TimeSpan) => (TimeSpan)value! + TimeSpan.FromSeconds(1),
        _ when type == typeof(TimeSpan?) => ((TimeSpan?)value ?? TimeSpan.Zero) + TimeSpan.FromSeconds(1),
        _ when type == typeof(DateTimeOffset) => (DateTimeOffset)value! + TimeSpan.FromDays(1),
        _ when type == typeof(DateTimeOffset?) => ((DateTimeOffset?)value ?? DateTimeOffset.UnixEpoch) + TimeSpan.FromDays(1),
        _ when type == typeof(Dictionary<string, int>) => new Dictionary<string, int>((Dictionary<string, int>)value!) { ["peer-1"] = 1 },
        _ => throw new NotSupportedException($"Teach this test a different value of {type}"),
    };
}
