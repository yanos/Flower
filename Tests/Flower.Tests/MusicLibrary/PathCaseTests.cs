using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Services;

namespace Flower.Tests.MusicLibrary;

// "Song.flac" and "song.flac" in one folder are two files where the folder's
// filesystem respects case - a Linux server's own disk - and one file where it
// does not - Windows, a default Mac, a NAS share over SMB. The library used to
// ignore case everywhere, so on Linux the second file never entered it at all,
// and one that did (the folder watcher adds files one at a time) took the
// first file's identity at the next rescan. docs/CODE-REVIEW-2026-10.md B2.
//
// The rules are given to the library directly rather than probed, so both
// filesystems are covered from whichever one the suite runs on.
public class PathCaseTests
{
    private const string Root = "/music";

    private static readonly PathRules RespectsCase = new([(Root, false)]);
    private static readonly PathRules IgnoresCase = new([(Root, true)]);

    private static Track File(string name, string title) => new()
    {
        Path = $"{Root}/Pixies/Doolittle/{name}", Title = title, Artists = "Pixies", Album = "Doolittle",
        Duration = TimeSpan.FromSeconds(172),
    };

    private static Library LibraryOf(PathRules rules, params Track[] tracks)
    {
        var library = new Library([], NullLogger<Library>.Instance);
        library.UsePathRules(rules);
        library.UpdateTracks([.. tracks]);
        return library;
    }

    [Fact]
    public void Where_case_is_respected_two_spellings_keep_their_own_identity_across_a_rescan()
    {
        var library = LibraryOf(RespectsCase, File("Debaser.flac", "Debaser"), File("debaser.flac", "Debaser (other rip)"));
        var upper = library.Tracks.Single(t => t.Path!.EndsWith("Debaser.flac", StringComparison.Ordinal));
        var lower = library.Tracks.Single(t => t.Path!.EndsWith("debaser.flac", StringComparison.Ordinal));
        upper.PlayCount = 10;
        lower.Starred = true;

        library.UpdateTracks([File("Debaser.flac", "Debaser"), File("debaser.flac", "Debaser (other rip)")]);

        Assert.Equal(2, library.Tracks.Select(t => t.Id).Distinct().Count());
        var upperAfter = library.Tracks.Single(t => t.Id == upper.Id);
        var lowerAfter = library.Tracks.Single(t => t.Id == lower.Id);
        Assert.Equal(10, upperAfter.PlayCount);
        Assert.False(upperAfter.Starred);
        Assert.Equal(0, lowerAfter.PlayCount);
        Assert.True(lowerAfter.Starred);
    }

    [Fact]
    public void Where_case_is_respected_removing_one_spelling_leaves_the_other_in()
    {
        var library = LibraryOf(RespectsCase, File("Debaser.flac", "Debaser"), File("debaser.flac", "Debaser (other rip)"));
        var lower = library.Tracks.Single(t => t.Path!.EndsWith("debaser.flac", StringComparison.Ordinal));

        library.RemoveTracks([lower], [lower.Path!]);
        library.UpdateTracks([File("Debaser.flac", "Debaser"), File("debaser.flac", "Debaser (other rip)")]);

        var left = Assert.Single(library.Tracks);
        Assert.EndsWith("Debaser.flac", left.Path);
    }

    // The other half, and why the comparison is per folder rather than per
    // platform: where the filesystem ignores case, the two spellings are one
    // file, and a scan that spells it differently - a folder re-added as
    // "/music" after "/Music" - is still that file and keeps its plays.
    [Fact]
    public void Where_case_is_ignored_a_new_spelling_is_the_same_song()
    {
        var library = LibraryOf(IgnoresCase, new Track
        {
            Path = $"{Root}/Untitled/track01.flac", Duration = TimeSpan.FromSeconds(90),
        });
        var before = Assert.Single(library.Tracks);
        before.PlayCount = 3;

        library.UpdateTracks([new Track { Path = $"{Root}/untitled/Track01.flac", Duration = TimeSpan.FromSeconds(90) }]);

        var after = Assert.Single(library.Tracks);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(3, after.PlayCount);
    }

    [Fact]
    public void Where_case_is_ignored_removing_one_spelling_removes_the_file()
    {
        var library = LibraryOf(IgnoresCase, File("Debaser.flac", "Debaser"));
        var track = Assert.Single(library.Tracks);

        library.RemoveTracks([track], [track.Path!]);

        Assert.True(library.IsExcludedPath($"{Root}/PIXIES/doolittle/debaser.FLAC"));
    }

    // A path under no library folder takes the platform's rule; one under a
    // folder takes that folder's, whatever the platform.
    [Fact]
    public void A_folders_rule_wins_over_the_platforms()
    {
        Assert.False(RespectsCase.Equals($"{Root}/a.flac", $"{Root}/A.flac"));
        Assert.True(IgnoresCase.Equals($"{Root}/a.flac", $"{Root}/A.flac"));
        Assert.Equal(
            IgnoresCase.GetHashCode($"{Root}/a.flac"),
            IgnoresCase.GetHashCode($"{Root}/A.flac"));
    }

    // Real files, so on whichever filesystem the suite runs: where both
    // spellings can exist the scan finds both, and where they cannot there is
    // only one file to find.
    [Fact]
    public void The_scan_finds_both_spellings_where_the_filesystem_holds_both()
    {
        var folder = Path.Combine(Path.GetTempPath(), "flower-case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var wav = Flower.DeviceChecks.SyntheticWav.Build(TimeSpan.FromMilliseconds(100), _ => 0);
            System.IO.File.WriteAllBytes(Path.Combine(folder, "Debaser.wav"), wav);
            // Where the second spelling is the first file again, a Mac
            // overwrites it, and the iOS simulator refuses the write outright
            // (IO_FileExists) - while File.Exists says no such file is there,
            // so asking first does not help. Either way the folder holds one
            // file, and that is what the scan is held to.
            try
            {
                System.IO.File.WriteAllBytes(Path.Combine(folder, "debaser.wav"), wav);
            }
            catch (IOException)
            {
            }
            var onDisk = Directory.GetFiles(folder).Length;

            var found = new Flower.Importer.Importer(NullLogger<Flower.Importer.Importer>.Instance).Import([folder]);

            Assert.Equal(onDisk, found.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
