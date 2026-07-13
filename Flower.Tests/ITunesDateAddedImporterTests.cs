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

    private static string TrackEntry(int id, string name, string artist, string? album, int totalTimeMs, string dateAddedIso)
    {
        var albumXml = album == null ? "" : $"<key>Album</key><string>{album}</string>";
        return $"""
            <key>{id}</key>
            <dict>
                <key>Track ID</key><integer>{id}</integer>
                <key>Name</key><string>{name}</string>
                <key>Artist</key><string>{artist}</string>
                {albumXml}
                <key>Total Time</key><integer>{totalTimeMs}</integer>
                <key>Date Added</key><date>{dateAddedIso}</date>
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
    public void ApplyFromXmlFile_leaves_DateAdded_unchanged_for_a_track_with_no_matching_entry()
    {
        var xmlPath = WriteLibraryXml(TrackEntry(1, "Some Other Song", "Some Artist", null, 60000, "2010-01-01T00:00:00Z"));
        var original = new DateTimeOffset(2019, 4, 4, 0, 0, 0, TimeSpan.Zero);
        var track = new Track { Title = "The Little Drummer Boy", Artists = "Deerhoof", Album = null, Duration = TimeSpan.FromSeconds(75), DateAdded = original };

        ITunesDateAddedImporter.ApplyFromXmlFile(new List<Track> { track }, xmlPath);

        Assert.Equal(original, track.DateAdded);
    }
}
