using System.Collections.Generic;

using Flower.Models;

namespace Flower.Tests;

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
    public void TotalPlayCount_ignores_no_remote_devices_by_default()
    {
        var track = new Track { PlayCount = 4, ImportedPlayCount = 1 };

        Assert.Equal(5, track.TotalPlayCount);
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
}
