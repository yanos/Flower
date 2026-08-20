using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Importer;
using Flower.Tests.TestSupport;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests;

// Importer had zero coverage (ARCHITECTURE-REVIEW.md §5.3). Fixtures are real
// files on disk read through real TagLib#, not a mocked tag layer - the whole
// point of this class is the file-system walk plus the tag mapping, and both
// are only meaningful against the actual library.
//
// Everything here is WAV, because SyntheticWav can produce one from scratch
// while mp3/m4a/flac would mean checking binary fixtures into the repo. TagLib#
// reads WAV as a TagLib.Riff.File, which supports writing a real ID3v2 tag into
// the RIFF container - so the tag mapping, including the Id3v2 branch of
// ReadIsCompilation, is exercised on the genuine code path. The Apple (m4a) and
// Xiph (flac) branches of that method are not reachable this way and stay
// uncovered.
//
// Import() with an empty/unresolvable path set scans nothing at all (it used to
// fall back to the platform music folder, which both walked the developer's real
// ~/Music from a test and made removing the last library folder impossible in
// the app) - see ImportsNothingWhenNoFoldersAreConfigured below.
public class ImporterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("flower-importer-tests").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file must not fail the test.
        }
    }

    private static Importer.Importer NewImporter() => new(NullLogger<Importer.Importer>.Instance);

    private string Dir(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    // A one-second silent WAV, optionally tagged. Named rather than positional
    // because most tests only care about one or two of the tag fields.
    private static string Audio(string directory, string fileName, Action<TagLib.File>? tag = null)
    {
        var path = SyntheticWav.CreateFile(directory, fileName, TimeSpan.FromSeconds(1), SyntheticWav.Marker(1));
        if (tag == null)
            return path;

        using var file = TagLib.File.Create(path);
        tag(file);
        file.Save();
        return path;
    }

    // The empty case has to stay empty rather than guessing a default music
    // folder: unchecking Settings > Library's iTunes integration works by
    // dropping Music.app's media folder from the configured list, and on a
    // default Mac that folder lives under ~/Music - so a fallback here would
    // re-scan exactly the tracks that were just removed. Both spellings of
    // "nothing configured" (no list at all, and a list of paths that don't
    // exist) go down the same branch. Skipped on iOS, where the sandboxed
    // Documents directory is scanned unconditionally instead - see Import.
    [Fact]
    public void ImportsNothingWhenNoFoldersAreConfigured()
    {
        if (OperatingSystem.IsIOS())
            return;
        Audio(_root, "ignored.wav");

        Assert.Empty(NewImporter().Import([]));
        Assert.Empty(NewImporter().Import(null));
        Assert.Empty(NewImporter().Import([Path.Combine(_root, "does-not-exist")]));
    }

    [Fact]
    public void Import_walks_subdirectories_recursively()
    {
        Audio(_root, "top.wav");
        Audio(Dir("artist"), "one.wav");
        Audio(Dir("artist", "album"), "two.wav");

        var tracks = NewImporter().Import([_root]);

        Assert.Equal(
            ["one.wav", "top.wav", "two.wav"],
            tracks.Select(t => Path.GetFileName(t.Path)).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Import_keeps_only_supported_audio_extensions()
    {
        Audio(_root, "keep.wav");
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "not audio");
        File.WriteAllText(Path.Combine(_root, "cover.jpg"), "not audio");
        File.WriteAllText(Path.Combine(_root, "song.ogg"), "unsupported format");

        var tracks = NewImporter().Import([_root]);

        Assert.Equal(["keep.wav"], tracks.Select(t => Path.GetFileName(t.Path)));
    }

    [Fact]
    public void Import_matches_extensions_case_insensitively()
    {
        Audio(_root, "shouty.WAV");

        var tracks = NewImporter().Import([_root]);

        Assert.Equal(["shouty.WAV"], tracks.Select(t => Path.GetFileName(t.Path)));
    }

    [Fact]
    public void Import_does_not_duplicate_a_file_reachable_from_two_configured_paths()
    {
        var nested = Dir("nested");
        Audio(nested, "shared.wav");

        // The nested path is inside the root path, so a naive walk would see
        // shared.wav twice.
        var tracks = NewImporter().Import([_root, nested]);

        Assert.Single(tracks);
    }

    [Fact]
    public void Import_ignores_blank_duplicate_and_nonexistent_configured_paths()
    {
        Audio(_root, "only.wav");
        var missing = Path.Combine(_root, "does-not-exist");

        var tracks = NewImporter().Import([_root, "  ", "", _root, missing]);

        Assert.Single(tracks);
    }

    [Fact]
    public void Import_skips_an_unreadable_file_and_keeps_the_rest()
    {
        Audio(_root, "good.wav");
        // A supported extension over bytes TagLib cannot parse - the routine
        // DRM'd/corrupt/truncated file in a real library.
        File.WriteAllBytes(Path.Combine(_root, "corrupt.mp3"), new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });

        var tracks = NewImporter().Import([_root]);

        Assert.Equal(["good.wav"], tracks.Select(t => Path.GetFileName(t.Path)));
    }

    [Fact]
    public void Import_maps_tag_fields_onto_the_track()
    {
        Audio(_root, "tagged.wav", f =>
        {
            f.Tag.Title = "Title";
            f.Tag.Album = "Album";
            f.Tag.Performers = ["First Artist", "Second Artist"];
            f.Tag.AlbumArtists = ["Album Artist"];
            f.Tag.Composers = ["Composer One", "Composer Two"];
            f.Tag.Genres = ["Jazz", "Fusion"];
            f.Tag.Year = 1975;
            f.Tag.Track = 3;
            f.Tag.TrackCount = 12;
            f.Tag.Disc = 1;
            f.Tag.DiscCount = 2;
            f.Tag.Comment = "Comment";
            f.Tag.BeatsPerMinute = 120;
        });

        var track = Assert.Single(NewImporter().Import([_root]));

        Assert.Equal("Title", track.Title);
        Assert.Equal("Album", track.Album);
        // Multi-valued tag fields are flattened to one comma-separated string.
        Assert.Equal("First Artist, Second Artist", track.Artists);
        Assert.Equal("Album Artist", track.AlbumArtists);
        Assert.Equal("Composer One, Composer Two", track.Composers);
        // Genre takes the first only, unlike the fields above.
        Assert.Equal("Jazz", track.Genre);
        Assert.Equal("1975", track.Year);
        Assert.Equal(3u, track.TrackNumber);
        Assert.Equal(12u, track.TrackCount);
        Assert.Equal(1u, track.DiscNumber);
        Assert.Equal(2u, track.DiscCount);
        Assert.Equal("Comment", track.Comment);
        Assert.Equal(120u, track.BeatsPerMinute);
        Assert.Equal(Path.Combine(_root, "tagged.wav"), track.Path);
    }

    [Fact]
    public void Import_maps_a_missing_year_to_null_rather_than_zero()
    {
        Audio(_root, "undated.wav");

        var track = Assert.Single(NewImporter().Import([_root]));

        Assert.Null(track.Year);
    }

    [Fact]
    public void Import_reads_audio_properties_off_the_file_itself()
    {
        Audio(_root, "props.wav");

        var track = Assert.Single(NewImporter().Import([_root]));

        Assert.Equal(TimeSpan.FromSeconds(1), track.Duration);
        Assert.Equal((int)Flower.Manager.GaplessFormat.SampleRate, track.SampleRate);
        Assert.Equal((int)Flower.Manager.GaplessFormat.Channels, track.Channels);
        Assert.False(string.IsNullOrEmpty(track.Codec));
    }

    [Fact]
    public void Import_reads_the_compilation_flag_from_the_id3v2_tag()
    {
        Audio(_root, "various.wav", f =>
        {
            var id3v2 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, create: true);
            id3v2.IsCompilation = true;
        });

        var track = Assert.Single(NewImporter().Import([_root]));

        Assert.True(track.IsCompilation);
    }

    [Fact]
    public void Import_leaves_the_compilation_flag_false_when_no_tag_sets_it()
    {
        Audio(_root, "single-artist.wav", f => f.Tag.Album = "Album");

        var track = Assert.Single(NewImporter().Import([_root]));

        Assert.False(track.IsCompilation);
    }
}
