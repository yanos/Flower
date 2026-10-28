using System;
using System.Collections.Generic;
using System.IO;
using Flower.Importer;
using Flower.Models;
using Flower.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.MusicLibrary.Import;

[Collection("PlatformDataDirectory")]
public class ITunesPlayCountImporterTests : IsolatedHome
{
    // Same shape Music.app's "export source 1 as XML" command produces -
    // ApplyFromXmlFile parses this exact structure.
    private static string WriteLibraryXml(string tracksXml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"itunes-export-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Tracks</key>
                <dict>
                    {tracksXml}
                </dict>
            </dict>
            </plist>
            """);
        return path;
    }

    private static string TrackEntry(int id, string name, string artist, string? album, int totalTimeMs, int playCount, string? location = null, string? playDateUtc = null)
    {
        var albumXml = album == null ? "" : $"<key>Album</key><string>{album}</string>";
        var locationXml = location == null ? "" : $"<key>Location</key><string>{location}</string>";
        var playDateXml = playDateUtc == null ? "" : $"<key>Play Date UTC</key><date>{playDateUtc}</date>";
        return $"""
            <key>{id}</key>
            <dict>
                <key>Track ID</key><integer>{id}</integer>
                <key>Name</key><string>{name}</string>
                <key>Artist</key><string>{artist}</string>
                {albumXml}
                <key>Total Time</key><integer>{totalTimeMs}</integer>
                <key>Play Count</key><integer>{playCount}</integer>
                {locationXml}
                {playDateXml}
            </dict>
            """;
    }

    // Music.app writes a Location URL for every track. Its path arrives in a
    // different Unicode composition from the one the same file has when read
    // off disk: decomposed ("e" + U+0301) there, precomposed (U+00E9) here.
    // Visually identical, ordinally different, so the dictionary lookup finds
    // nothing and the play count silently does not transfer - which is the bug
    // NormalizePath was written against, confirmed on a real file.
    //
    // This is also a guard on a build setting. NormalizePath is
    // string.Normalize, and under InvariantGlobalization that is a no-op which
    // returns its input while IsNormalized reports success - no exception, just
    // a match that stops happening. Flower.Server ran that way and had this
    // quietly broken, hence the comment now sitting in Flower.Server.csproj. If
    // someone re-adds the switch, this test is what should stop them.
    [Fact]
    public void ApplyFromXmlFile_matches_a_path_whose_accents_are_composed_differently()
    {
        const string decomposed = "/Music/Cafe\u0301 Bleu/Song.mp3";   // e + combining acute
        const string precomposed = "/Music/Caf\u00e9 Bleu/Song.mp3";   // single codepoint

        Assert.NotEqual(decomposed, precomposed);   // the whole premise

        // Three slashes, i.e. an empty host, which is what Music.app writes.
        // With a host ("file://localhost/...") Uri.LocalPath hands back a UNC
        // path with backslashes on every OS, and no POSIX path ever matches it.

        var xmlPath = WriteLibraryXml($"""
            <key>1</key>
            <dict>
                <key>Track ID</key><integer>1</integer>
                <key>Name</key><string>Song</string>
                <key>Artist</key><string>Unrelated Artist</string>
                <key>Total Time</key><integer>1000</integer>
                <key>Play Count</key><integer>12</integer>
                <key>Location</key><string>file://{Uri.EscapeDataString(decomposed).Replace("%2F", "/")}</string>
            </dict>
            """);

        // Deliberately mismatched title/artist/duration, so nothing but the
        // path can produce a match and the metadata fallback cannot rescue it.
        var track = new Track
        {
            Title = "Something Else", Artists = "Someone Else",
            Duration = TimeSpan.FromMinutes(9), Path = precomposed,
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(12, track.ImportedPlayCount);
    }

    [Fact]
    public void ApplyFromXmlFile_sets_ImportedPlayCount_from_a_matching_entry()
    {
        var xmlPath = WriteLibraryXml(TrackEntry(1, "The Little Drummer Boy", "Deerhoof", null, 75023, 7));
        var track = new Track { Title = "The Little Drummer Boy", Artists = "Deerhoof", Album = null, Duration = TimeSpan.FromSeconds(75.031) };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(7, track.ImportedPlayCount);
    }

    // Confirmed against a real library: Music.app can carry two separate
    // Track entries for the exact same file on disk (a leftover from a
    // library merge/duplicate import - e.g. two "Wishwanderer" entries, same
    // Location, play counts 19 and 1). Both collapse onto the one Flower
    // Track's SyncKey, so the correct total is the sum of both, not
    // whichever entry the importer happened to see last.
    [Fact]
    public void ApplyFromXmlFile_sums_play_counts_from_duplicate_entries_with_the_same_sync_key()
    {
        var xmlPath = WriteLibraryXml(
            TrackEntry(40393, "Wishwanderer", "Vashti Bunyan", "Singles And Demos", 118320, 19) +
            TrackEntry(47307, "Wishwanderer", "Vashti Bunyan", "Singles And Demos", 118320, 1));
        var track = new Track { Title = "Wishwanderer", Artists = "Vashti Bunyan", Album = "Singles And Demos", Duration = TimeSpan.FromMilliseconds(118320) };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(20, track.ImportedPlayCount);
    }

    [Fact]
    public void ApplyFromXmlFile_leaves_ImportedPlayCount_unset_for_a_track_with_no_matching_entry()
    {
        var xmlPath = WriteLibraryXml(TrackEntry(1, "Some Other Song", "Some Artist", null, 60000, 3));
        var track = new Track { Title = "The Little Drummer Boy", Artists = "Deerhoof", Album = null, Duration = TimeSpan.FromSeconds(75) };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(0, track.ImportedPlayCount);
    }

    // These test ApplyFromXmlFile directly, against an explicit synthetic XML
    // path, rather than the full Apply(tracks) entry point - Apply() always
    // tries a *live* AppleScript export from Music.app first (see
    // ITunesPlayCountImporter's class comment) and wins on any machine that
    // actually has Music.app installed, which would make these tests see real
    // library data instead of the synthetic fixture below.
    [Fact]
    public void ITunesPlayCountImporter_falls_back_to_title_artist_album_when_duration_disagrees_but_is_unambiguous()
    {
        var xmlPath = Path.Combine(Home, "sample-library.xml");
        File.WriteAllText(xmlPath, SampleLibraryXml(17));

        // Same title/artist/album as the XML entry, but a very different
        // length - confirmed against a real VBR-encoded MP3 where TagLib's
        // parsed duration and Music.app's own recorded Total Time disagreed
        // by ~10 minutes (a known old-iTunes VBR-header mis-parse), not a
        // rounding-boundary fraction of a second. There's only one candidate
        // in the XML at this title/artist/album, so Track.BuildLooseKey's
        // fallback (see its own doc comment) still matches it.
        var track = new Track
        {
            Title = "Test Song", Artists = "Test Artist", Album = "Test Album",
            Duration = TimeSpan.FromSeconds(45),
            Path = "/music/song.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(17, track.ImportedPlayCount);
    }

    [Fact]
    public void ITunesPlayCountImporter_does_not_guess_between_two_entries_with_different_durations()
    {
        var xmlPath = Path.Combine(Home, "sample-library.xml");
        // Two distinct XML entries share the same title/artist/album but have
        // different durations from each other (and from the local track below)
        // - genuinely ambiguous (could be, say, a studio cut and a live version
        // sharing sloppy tags), so neither the exact key nor the loose-key
        // fallback should guess which one the local track corresponds to.
        File.WriteAllText(xmlPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Tracks</key>
                <dict>
                    <key>1001</key>
                    <dict>
                        <key>Name</key><string>Test Song</string>
                        <key>Artist</key><string>Test Artist</string>
                        <key>Album</key><string>Test Album</string>
                        <key>Total Time</key><integer>200000</integer>
                        <key>Play Count</key><integer>17</integer>
                    </dict>
                    <key>1002</key>
                    <dict>
                        <key>Name</key><string>Test Song</string>
                        <key>Artist</key><string>Test Artist</string>
                        <key>Album</key><string>Test Album</string>
                        <key>Total Time</key><integer>300000</integer>
                        <key>Play Count</key><integer>9</integer>
                    </dict>
                </dict>
            </dict>
            </plist>
            """);

        var track = new Track
        {
            Title = "Test Song", Artists = "Test Artist", Album = "Test Album",
            Duration = TimeSpan.FromSeconds(45),
            Path = "/music/song.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(0, track.ImportedPlayCount);
    }

    [Fact]
    public void ITunesPlayCountImporter_matches_by_path_when_metadata_disagrees()
    {
        // Confirmed against a real track whose Artist tag had been edited to
        // add a native-language name ("Takashi Kokubo (小久保隆)") after
        // Music.app last indexed it, leaving Music.app's own record at plain
        // "Takashi Kokubo" - metadata-based matching (exact or loose) can
        // never bridge a genuine content difference like this, but Location
        // still points at the exact same file, so path match (tried first)
        // does.
        var xmlPath = Path.Combine(Home, "sample-library.xml");
        File.WriteAllText(xmlPath, """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Tracks</key>
                <dict>
                    <key>1001</key>
                    <dict>
                        <key>Name</key><string>Song</string>
                        <key>Artist</key><string>Old Artist Name</string>
                        <key>Album</key><string>Album</string>
                        <key>Total Time</key><integer>75023</integer>
                        <key>Play Count</key><integer>17</integer>
                        <key>Location</key><string>file:///Users/test/Music/Music/Media.localized/Music/Artist/Album/01%20Song.mp3</string>
                    </dict>
                </dict>
            </dict>
            </plist>
            """);

        var track = new Track
        {
            Title = "Song", Artists = "New Artist Name (Native Name)", Album = "Album", Duration = TimeSpan.FromSeconds(75.031),
            Path = "/Users/test/Music/Music/Media.localized/Music/Artist/Album/01 Song.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(17, track.ImportedPlayCount);
    }

    // A song this device only streams has no Path, only where the file sits
    // under its server's music folder - and a server filled from Music.app
    // keeps it under the same artist and album folders. Confirmed against a
    // real library: an album tagged "The Rip Tide 2011" that Music.app calls
    // "The Rip Tide" matched on no key, and sat in a "Never played" smart
    // playlist after eleven plays a song.
    [Fact]
    public void A_streamed_song_is_found_by_where_it_sits_under_the_servers_music_folder()
    {
        var xmlPath = WriteLibraryXml(TrackEntry(
            1, "Santa Fe", "Beirut", "The Rip Tide", 254432, 12,
            location: "file:///Users/test/Music/Music/Media.localized/Music/Beirut/The%20Rip%20Tide/02%20Santa%20Fe.mp3"));
        var track = new Track
        {
            Title = "Santa Fe", Artists = "Beirut", Album = "The Rip Tide 2011", Duration = TimeSpan.FromSeconds(254.43),
            OriginRelativePath = "Beirut/The Rip Tide/02 Santa Fe.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(12, track.ImportedPlayCount);
    }

    // A relative path ending two Music.app files names neither of them.
    [Fact]
    public void A_server_path_that_ends_two_Music_app_files_is_not_guessed_between()
    {
        var xmlPath = WriteLibraryXml(
            TrackEntry(1, "Intro", "One", "First", 60000, 5, location: "file:///Users/test/Music/One/First/01%20Intro.mp3")
            + TrackEntry(2, "Intro", "Two", "Second", 90000, 9, location: "file:///Users/test/Other/Music/One/First/01%20Intro.mp3"));
        var track = new Track
        {
            Title = "Intro", Artists = "Someone Else", Album = "Elsewhere", Duration = TimeSpan.FromSeconds(60),
            OriginRelativePath = "One/First/01 Intro.mp3",
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(0, track.ImportedPlayCount);
    }

    [Fact]
    public void Music_apps_last_play_is_taken_for_a_song_Flower_has_not_played_since()
    {
        var xmlPath = WriteLibraryXml(
            TrackEntry(1, "Never Here", "Artist", "Album", 60000, 4, playDateUtc: "2019-05-04T20:00:00Z")
            + TrackEntry(2, "Played Before", "Artist", "Album", 90000, 7, playDateUtc: "2024-03-01T12:00:00Z"));
        var neverPlayedInFlower = new Track { Title = "Never Here", Artists = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(60) };
        var playedEarlierInFlower = new Track
        {
            Title = "Played Before", Artists = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(90),
            LastPlayedAt = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { neverPlayedInFlower, playedEarlierInFlower }, xmlPath);

        Assert.Equal(new DateTimeOffset(2019, 5, 4, 20, 0, 0, TimeSpan.Zero), neverPlayedInFlower.LastPlayedAt);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero), playedEarlierInFlower.LastPlayedAt);
    }

    [Fact]
    public void A_listen_in_Flower_after_Music_apps_last_play_is_kept()
    {
        var xmlPath = WriteLibraryXml(TrackEntry(1, "Song", "Artist", "Album", 60000, 4, playDateUtc: "2019-05-04T20:00:00Z"));
        var flowerPlayed = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);
        var track = new Track { Title = "Song", Artists = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(60), LastPlayedAt = flowerPlayed };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(flowerPlayed, track.LastPlayedAt);
    }

    [Fact]
    public void ITunesPlayCountImporter_leaves_ImportedPlayCount_alone_for_a_nonexistent_file()
    {
        var track = new Track { Title = "Test Song", Path = "/music/song.mp3", ImportedPlayCount = 3 };

        ITunesPlayCountImporter.ApplyFromXmlFile(new List<Track> { track }, Path.Combine(Home, "does-not-exist.xml"));

        Assert.Equal(3, track.ImportedPlayCount);
    }

    // 200 seconds (200000 ms) - matches Track.BuildSyncKey's (int)Duration.TotalSeconds
    // truncation on the Flower side for a Duration of exactly 200 seconds.
    private static string SampleLibraryXml(int playCount) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Tracks</key>
            <dict>
                <key>1001</key>
                <dict>
                    <key>Name</key><string>Test Song</string>
                    <key>Artist</key><string>Test Artist</string>
                    <key>Album</key><string>Test Album</string>
                    <key>Total Time</key><integer>200000</integer>
                    <key>Play Count</key><integer>{playCount}</integer>
                    <key>Location</key><string>file:///Users/someone/Music/iTunes/iTunes%20Music/Test%20Artist/song.mp3</string>
                </dict>
            </dict>
        </dict>
        </plist>
        """;
}
