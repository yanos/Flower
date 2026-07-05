using System;
using System.Collections.Generic;
using System.IO;
using Flower.Importer;
using Flower.Models;

namespace Flower.Tests;

public class ITunesPlayCountImporterTests
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

    private static string TrackEntry(int id, string name, string artist, string? album, int totalTimeMs, int playCount)
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
                <key>Play Count</key><integer>{playCount}</integer>
            </dict>
            """;
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
}
