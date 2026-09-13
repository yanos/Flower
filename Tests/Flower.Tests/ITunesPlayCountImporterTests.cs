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
}
