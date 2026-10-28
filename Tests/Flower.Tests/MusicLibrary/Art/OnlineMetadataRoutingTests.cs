using System;

using Flower.Models;
using Flower.Services;

using Xunit;

namespace Flower.Tests.MusicLibrary.Art;

// Who an artist's picture is asked of: the paired server for an artist it has
// songs by, this device for one that is only this device's - a guest's own
// files, or everything on a device that is on its own.
public sealed class OnlineMetadataRoutingTests
{
    private const string Server = "server-fp";

    private static Track Song(string? origin = null) => new()
    {
        Title = "Song",
        Artists = "Ohmme",
        Album = "Parts",
        Path = origin == null ? "/music/ohmme/parts/song.flac" : null,
        OriginDeviceFingerprint = origin,
        OriginTrackId = origin == null ? null : "tr-1",
    };

    [Fact]
    public void An_artist_the_server_has_songs_by_is_the_servers_to_look_up()
    {
        Assert.True(RoutedOnlineMetadata.UsesServer([Song(), Song(Server)], Server));
    }

    [Fact]
    public void An_artist_only_this_device_has_is_looked_up_here()
    {
        Assert.False(RoutedOnlineMetadata.UsesServer([Song()], Server));
    }

    [Fact]
    public void A_server_this_device_is_no_longer_paired_with_is_not_asked()
    {
        Assert.False(RoutedOnlineMetadata.UsesServer([Song("old-server-fp")], Server));
        Assert.False(RoutedOnlineMetadata.UsesServer([Song(Server)], null));
    }
}
