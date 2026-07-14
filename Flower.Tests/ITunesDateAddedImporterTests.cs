using System;
using System.Collections.Generic;
using System.IO;
using Flower.Importer;
using Flower.Models;

namespace Flower.Tests;

public class ITunesDateAddedImporterTests
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

    private static string TrackEntry(int id, string name, string artist, string? album, int totalTimeMs, string dateAddedIso, string? location = null)
    {
        var albumXml = album == null ? "" : $"<key>Album</key><string>{album}</string>";
        var locationXml = location == null ? "" : $"<key>Location</key><string>{location}</string>";
        return $"""
            <key>{id}</key>
            <dict>
                <key>Track ID</key><integer>{id}</integer>
                <key>Name</key><string>{name}</string>
                <key>Artist</key><string>{artist}</string>
                {albumXml}
                <key>Total Time</key><integer>{totalTimeMs}</integer>
                <key>Date Added</key><date>{dateAddedIso}</date>
                {locationXml}
            </dict>
            """;
    }

    [Fact]
    public void ApplyFromXmlFile_adopts_an_older_iTunes_date_than_the_tracks_current_DateAdded()
    {
        var xmlPath = WriteLibraryXml(TrackEntry(1, "The Little Drummer Boy", "Deerhoof", null, 75023, "2010-01-01T00:00:00Z"));
        var track = new Track
        {
            Title = "The Little Drummer Boy", Artists = "Deerhoof", Album = null, Duration = TimeSpan.FromSeconds(75.031),
            DateAdded = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero), track.DateAdded);
    }

    [Fact]
    public void ApplyFromXmlFile_keeps_the_tracks_own_DateAdded_when_it_is_already_older()
    {
        var xmlPath = WriteLibraryXml(TrackEntry(1, "The Little Drummer Boy", "Deerhoof", null, 75023, "2020-06-01T00:00:00Z"));
        var original = new DateTimeOffset(2015, 3, 2, 10, 15, 0, TimeSpan.Zero);
        var track = new Track
        {
            Title = "The Little Drummer Boy", Artists = "Deerhoof", Album = null, Duration = TimeSpan.FromSeconds(75.031),
            DateAdded = original,
        };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(original, track.DateAdded);
    }

    // Mirrors ITunesPlayCountImporterTests' identical duplicate-entry case -
    // Music.app can carry two Track entries for the same file (a leftover from
    // a library merge). The earliest of the duplicates is the correct "first
    // added" date, not whichever the importer happened to see last.
    [Fact]
    public void ApplyFromXmlFile_takes_the_earliest_date_among_duplicate_entries_with_the_same_sync_key()
    {
        var xmlPath = WriteLibraryXml(
            TrackEntry(40393, "Wishwanderer", "Vashti Bunyan", "Singles And Demos", 118320, "2018-05-01T00:00:00Z") +
            TrackEntry(47307, "Wishwanderer", "Vashti Bunyan", "Singles And Demos", 118320, "2012-02-14T00:00:00Z"));
        var track = new Track
        {
            Title = "Wishwanderer", Artists = "Vashti Bunyan", Album = "Singles And Demos", Duration = TimeSpan.FromMilliseconds(118320),
            DateAdded = new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(new DateTimeOffset(2012, 2, 14, 0, 0, 0, TimeSpan.Zero), track.DateAdded);
    }

    [Fact]
    public void ApplyFromXmlFile_matches_by_path_when_metadata_disagrees()
    {
        // Confirmed against a real track whose Artist tag had been edited to
        // add a native-language name ("Takashi Kokubo (小久保隆)") after
        // Music.app last indexed it, leaving Music.app's own record at plain
        // "Takashi Kokubo" - metadata-based matching (exact or loose) can
        // never bridge a genuine content difference like this, but Location
        // still points at the exact same file, so path match (tried first)
        // does.
        var xmlPath = WriteLibraryXml(TrackEntry(
            1, "Song", "Old Artist Name", "Album", 75023, "2010-01-01T00:00:00Z",
            location: "file:///Users/test/Music/Music/Media.localized/Music/Artist/Album/01%20Song.mp3"));
        var track = new Track
        {
            Title = "Song", Artists = "New Artist Name (Native Name)", Album = "Album", Duration = TimeSpan.FromSeconds(75.031),
            Path = "/Users/test/Music/Music/Media.localized/Music/Artist/Album/01 Song.mp3",
            DateAdded = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero), track.DateAdded);
    }

    [Fact]
    public void ApplyFromXmlFile_matches_by_path_despite_different_unicode_normalization()
    {
        // Confirmed against a real file whose name contains "é": iTunes'
        // Location URL had it as the decomposed form ("e" + a combining
        // acute accent, U+0301 - written here as "é") while the local
        // Track.Path used the precomposed single-codepoint form ("é") -
        // visually identical, but byte-for-byte different, so the path match
        // silently found nothing until both sides were normalized the same way.
        var xmlPath = WriteLibraryXml(TrackEntry(
            1, "Song", "Artist", "Album", 75023, "2008-01-01T00:00:00Z",
            location: "file:///Users/test/Music/Music/Media.localized/Music/Artist/Album/01%20De%CC%81ja.mp3"));
        var track = new Track
        {
            Title = "Song", Artists = "Artist", Album = "Album", Duration = TimeSpan.FromSeconds(75.031),
            Path = "/Users/test/Music/Music/Media.localized/Music/Artist/Album/01 Déja.mp3",
            DateAdded = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(new DateTimeOffset(2008, 1, 1, 0, 0, 0, TimeSpan.Zero), track.DateAdded);
    }

    [Fact]
    public void ApplyFromXmlFile_falls_back_to_title_artist_album_when_duration_disagrees_but_is_unambiguous()
    {
        // Same title/artist/album as the XML entry, but a very different
        // length - confirmed against a real VBR-encoded MP3 where TagLib's
        // parsed duration and Music.app's own recorded Total Time disagreed
        // by ~10 minutes (a known old-iTunes VBR-header mis-parse), not a
        // rounding-boundary fraction of a second. There's only one candidate
        // in the XML at this title/artist/album, so Track.BuildLooseKey's
        // fallback (see its own doc comment) still matches it.
        var xmlPath = WriteLibraryXml(TrackEntry(1, "The Little Drummer Boy", "Deerhoof", null, 75023, "2010-01-01T00:00:00Z"));
        var track = new Track
        {
            Title = "The Little Drummer Boy", Artists = "Deerhoof", Album = null, Duration = TimeSpan.FromMinutes(20),
            DateAdded = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero), track.DateAdded);
    }

    [Fact]
    public void ApplyFromXmlFile_does_not_guess_between_two_entries_with_different_durations()
    {
        // Two distinct XML entries share the same title/artist/album but have
        // different durations from each other (and from the local track below)
        // - genuinely ambiguous (could be, say, a studio cut and a live version
        // sharing sloppy tags), so neither the exact key nor the loose-key
        // fallback should guess which one the local track corresponds to.
        var xmlPath = WriteLibraryXml(
            TrackEntry(1, "The Little Drummer Boy", "Deerhoof", null, 75023, "2010-01-01T00:00:00Z") +
            TrackEntry(2, "The Little Drummer Boy", "Deerhoof", null, 120000, "2005-01-01T00:00:00Z"));
        var original = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var track = new Track
        {
            Title = "The Little Drummer Boy", Artists = "Deerhoof", Album = null, Duration = TimeSpan.FromMinutes(20),
            DateAdded = original,
        };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(original, track.DateAdded);
    }

    [Fact]
    public void ApplyFromXmlFile_leaves_DateAdded_unchanged_for_a_track_with_no_matching_entry()
    {
        var xmlPath = WriteLibraryXml(TrackEntry(1, "Some Other Song", "Some Artist", null, 60000, "2010-01-01T00:00:00Z"));
        var original = new DateTimeOffset(2019, 4, 4, 0, 0, 0, TimeSpan.Zero);
        var track = new Track { Title = "The Little Drummer Boy", Artists = "Deerhoof", Album = null, Duration = TimeSpan.FromSeconds(75), DateAdded = original };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(original, track.DateAdded);
    }
}
