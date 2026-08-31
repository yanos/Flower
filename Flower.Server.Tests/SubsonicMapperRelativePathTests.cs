using Flower.Models;
using Flower.Server.Services;

namespace Flower.Server.Tests;

// Child.RelativePath is the one part of a served file's path that crosses the
// wire (SYNC-PLAN.md's Path-can't-cross-the-wire rule covers the rest), and a
// client names its downloaded copy after it - see LibraryDownloadService. So
// what matters here is as much what it leaves out as what it includes.
public class SubsonicMapperRelativePathTests
{
    private static Track At(string path) => new() { Title = "Fabienk", Path = path };

    [Fact]
    public void The_configured_root_is_stripped_and_the_rest_is_kept()
    {
        var relative = SubsonicMapper.RelativePathOf(
            At("/srv/music/Angine de Poitrine/Vol.II/01 Fabienk.mp3"),
            ["/srv/music"]);

        Assert.Equal("Angine de Poitrine/Vol.II/01 Fabienk.mp3", relative);
    }

    [Fact]
    public void A_trailing_separator_on_the_configured_root_makes_no_difference()
    {
        var relative = SubsonicMapper.RelativePathOf(
            At("/srv/music/Artist/Album/Song.mp3"),
            ["/srv/music/"]);

        Assert.Equal("Artist/Album/Song.mp3", relative);
    }

    // The shorter root would prepend a directory the deeper one already
    // accounts for, so the file would be saved one level too deep.
    [Fact]
    public void The_longest_matching_root_wins_for_nested_folders()
    {
        var relative = SubsonicMapper.RelativePathOf(
            At("/srv/music/lossless/Artist/Song.flac"),
            ["/srv/music", "/srv/music/lossless"]);

        Assert.Equal("Artist/Song.flac", relative);
    }

    // A file scanned before its folder was removed from the configuration, or
    // one adopted from Music.app. The name is still worth sending; the
    // directories it sits in are not ours to describe.
    [Fact]
    public void A_file_under_no_configured_root_sends_its_name_and_nothing_else()
    {
        var relative = SubsonicMapper.RelativePathOf(
            At("/Users/someone/Music/Media.localized/Music/Artist/Album/01 Song.mp3"),
            ["/srv/music"]);

        Assert.Equal("01 Song.mp3", relative);
    }

    [Fact]
    public void A_placeholder_has_no_file_and_so_no_relative_path()
    {
        Assert.Null(SubsonicMapper.RelativePathOf(new Track { Title = "Fabienk" }, ["/srv/music"]));
    }

    [Fact]
    public void No_configured_roots_at_all_still_sends_the_name()
    {
        Assert.Equal("01 Song.mp3", SubsonicMapper.RelativePathOf(At("/srv/music/01 Song.mp3"), null));
    }
}
