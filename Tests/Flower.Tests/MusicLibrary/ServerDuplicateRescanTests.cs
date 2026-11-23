using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;

using Xunit;

namespace Flower.Tests.MusicLibrary;

// A server holding two files that tag identically, one of them on this device.
// The other is a different song to the server, so a rescan must not take it
// for a duplicate of the file here and drop it for the next sync to add back.
public class ServerDuplicateRescanTests
{
    private const string Server = "server-fingerprint";

    private static Track Tagged(string? path, string? originId) => new()
    {
        Title = "3 Sisters", Artists = "Waxahatchee", Album = "Tigers Blood",
        Duration = TimeSpan.FromSeconds(251), Path = path,
        OriginDeviceFingerprint = originId == null ? null : Server,
        OriginTrackId = originId,
        IsLocallyDownloaded = path != null && originId != null,
    };

    private static Track Scanned(string path) => new()
    {
        Title = "3 Sisters", Artists = "Waxahatchee", Album = "Tigers Blood",
        Duration = TimeSpan.FromSeconds(251), Path = path,
    };

    private static Library LibraryOf(params Track[] tracks) =>
        new(new List<Track>(tracks), NullLogger<Library>.Instance);

    [Fact]
    public void The_servers_other_copy_survives_a_rescan_with_its_identity()
    {
        var other = Tagged(null, "server-copy-2");
        var library = LibraryOf(Tagged("/music/01. 3 Sisters.mp3", "server-copy-1"), other);

        library.UpdateTracks([Scanned("/music/01. 3 Sisters.mp3")]);

        Assert.Equal(2, library.Tracks.Count);
        Assert.Contains(library.Tracks, t => t.Id == other.Id && t.Path == null);
    }

    [Fact]
    public void A_file_that_moved_keeps_its_own_identity_beside_the_servers_other_copy()
    {
        var mine = Tagged("/music/old/01. 3 Sisters.mp3", "server-copy-1");
        var other = Tagged(null, "server-copy-2");
        var library = LibraryOf(other, mine);

        library.UpdateTracks([Scanned("/music/new/01. 3 Sisters.mp3")]);

        var moved = library.Tracks.Single(t => t.Path == "/music/new/01. 3 Sisters.mp3");
        Assert.Equal("server-copy-1", moved.OriginTrackId);
        Assert.Contains(library.Tracks, t => t.Id == other.Id);
    }

    [Fact]
    public void A_placeholder_for_the_file_found_here_is_still_folded_into_it()
    {
        var library = LibraryOf(Tagged(null, "server-copy-1"));

        library.UpdateTracks([Scanned("/music/01. 3 Sisters.mp3")]);

        var only = Assert.Single(library.Tracks);
        Assert.Equal("server-copy-1", only.OriginTrackId);
    }
}
