using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Claunia.PropertyList;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Services;

namespace Flower.Importer
{
    public class Importer : IMusicImporter
    {
        private readonly ILogger<Importer> _logger;
        private static readonly HashSet<string> ValidExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ".mp3", ".m4a", ".wav", ".flac", ".alac",
                ".ogg", ".opus", ".aiff", ".aif", ".ape",
            };

        // Whether a scan would take this file at all, by its name alone - the
        // same question an upload has to answer before accepting one (see
        // LibraryIngest), so that nothing lands in a library folder that the
        // next scan would walk straight past.
        public static bool IsImportable(string fileName) => ValidExtensions.Contains(Path.GetExtension(fileName));

        public Importer(ILogger<Importer> logger)
        {
            _logger = logger;
        }

        public Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null)
            => Task.Run(() => Import(libraryPaths));

        public List<Track> Import(IEnumerable<string>? libraryPaths = null)
        {
            var tracks = new List<Track>();

            var paths = ScanRoots(libraryPaths);
            if (paths.Count == 0)
            {
                _logger.LogInformation("No library folders configured - nothing to scan");
                return tracks;
            }

            // By each folder's own rule (PathRules). Ignoring case everywhere
            // took "Song.flac" for "song.flac" on a filesystem where they are
            // two files, and the second was never seen at all.
            var seenFiles = new HashSet<string>(PathRules.For(paths));

            foreach (var path in paths)
            {
                ImportFrom(path, tracks, seenFiles);
            }

            return tracks;
        }

        // The folders a scan of these settings actually walks - public because
        // "which folder is this file under" is asked outside a scan too: what
        // an upload calls the file on the server is its path below one of
        // these (see LibraryMirrorService).
        public static List<string> ScanRoots(IEnumerable<string>? libraryPaths)
        {
            var configured = libraryPaths?
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // No configured folders means an empty library, not "guess the
            // user's music folder". The folder list (Settings > Library) is the
            // whole of what the user has asked Flower to scan, so emptying it -
            // which now takes removing the last folder by hand, that being the
            // only thing that removes one (see SettingsViewModel.
            // ApplyAppleMusicFolder) - has to actually empty the library.
            // Guessing here defeated that: on a default Mac setup
            // Music.app's media folder sits *under* ~/Music, so falling back to
            // ~/Music re-scanned exactly the tracks that had just been removed.
            // The default folder a first run starts with is seeded into the
            // settings instead (again AppSettingsStore.Load), where it is
            // visible and removable rather than an invisible floor.
            //
            // iOS is the one exception, and isn't really a fallback: the app's
            // sandboxed Documents directory is the only place it can read files
            // from at all (see Info.plist UIFileSharingEnabled), not a user
            // choice, and its absolute path is deliberately not persisted - the
            // container UUID can change across a reinstall (see
            // Library.UpdateTracks' SyncKey fallback).
            if (configured is { Count: > 0 })
                return configured;
            if (OperatingSystem.IsIOS())
                return [Environment.GetFolderPath(Environment.SpecialFolder.Personal)];

            return [];
        }

        // Anything below a folder whose name starts with a dot. Those are
        // other programs' bookkeeping, never the user's music: a trash folder
        // above all - macOS's .Trashes at the root of a volume, and the
        // .Trash-<uid> a Linux desktop (or Flower.Server, see FileTrash) makes
        // at the top of a drive the home folder is not on. Scanned, a song
        // moved to the trash on purpose came straight back with the next scan.
        // The configured root itself is exempt: a library the user pointed at
        // a dot-folder is one they meant.
        public static bool IsUnderHiddenFolder(string root, string file)
        {
            var relative = Path.GetRelativePath(root, file);
            var folders = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            for (var i = 0; i < folders.Length - 1; i++)
            {
                if (folders[i].StartsWith('.') && folders[i] != "..")
                    return true;
            }

            return false;
        }

        private void ImportFrom(string path, List<Track> tracks, HashSet<string> seenFiles)
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(IsImportable)
                .Where(f => !IsUnderHiddenFolder(path, f));

            // What each folder's cover file measures, so an album with no
            // embedded art lists its folder once rather than once per song.
            var coverFiles = new Dictionary<string, ArtworkDimensions>(StringComparer.Ordinal);

            foreach (var file in files)
            {
                // Skip files already imported from an overlapping configured path.
                if (!seenFiles.Add(file))
                    continue;

                if (ImportFile(file, coverFiles) is { } track)
                    tracks.Add(track);
            }
        }

        // One file read into a Track, or null when it cannot be - the per-file
        // half of a scan, and the whole of what an upload needs (see
        // LibraryIngest), which has exactly one new file to read and no reason
        // to walk the rest of the library to find it.
        public Track? ImportFile(string file) => ImportFile(file, coverFiles: null);

        private Track? ImportFile(string file, IDictionary<string, ArtworkDimensions>? coverFiles)
        {
            try
            {
                var tagFile = TagLib.File.Create(file);
                var tag = tagFile.Tag;
                var props = tagFile.Properties;
                var technical = AudioTechnicalProperties.From(props);
                var id3v23 = Id3v23(tagFile);
                var artwork = ArtworkDimensions.Read(tagFile, file, coverFiles);

                return new Track
                {
                    // Core identity
                    Title         = tag.Title,
                    TitleSort     = tag.TitleSort,
                    Subtitle      = tag.Subtitle,
                    Artists       = SlashSeparated(id3v23, "TPE1") ?? string.Join(", ", tag.Performers),
                    ArtistsSort   = SlashSeparated(id3v23, "TSOP") ?? JoinOrNull(tag.PerformersSort),
                    AlbumArtists  = SlashSeparated(id3v23, "TPE2") ?? string.Join(", ", tag.AlbumArtists),
                    IsCompilation = CompilationFlag.Read(tagFile),
                    Album         = tag.Album,
                    AlbumSort     = tag.AlbumSort,
                    Year          = tag.Year > 0 ? tag.Year.ToString() : null,
                    TrackNumber   = tag.Track,
                    TrackCount    = tag.TrackCount,
                    DiscNumber    = tag.Disc,
                    DiscCount     = tag.DiscCount,

                    // People
                    Composers     = SlashSeparated(id3v23, "TCOM") ?? string.Join(", ", tag.Composers),
                    ComposersSort = SlashSeparated(id3v23, "TSOC") ?? JoinOrNull(tag.ComposersSort),
                    Conductor     = SlashSeparated(id3v23, "TPE3") ?? tag.Conductor,
                    RemixedBy     = SlashSeparated(id3v23, "TPE4") ?? tag.RemixedBy,

                    // Classification
                    Genre            = tag.FirstGenre,
                    BeatsPerMinute   = tag.BeatsPerMinute,
                    InitialKey       = tag.InitialKey,
                    Grouping         = tag.Grouping,
                    Publisher        = tag.Publisher,
                    ISRC             = tag.ISRC,

                    // Descriptions
                    Comment      = tag.Comment,
                    Description  = tag.Description,
                    Copyright    = tag.Copyright,
                    Lyrics       = tag.Lyrics,

                    // Audio technical
                    Duration       = props?.Duration ?? TimeSpan.Zero,
                    Bitrate        = technical.Bitrate,
                    SampleRate     = technical.SampleRate,
                    Channels       = technical.Channels,
                    BitsPerSample  = technical.BitsPerSample,
                    Codec          = technical.Codec,
                    // A second, small read of the file's head (the first
                    // MPEG frame - see EncodingProfile), because this is
                    // not in the tag and TagLib's Properties describe the
                    // decoded stream, not how it was produced.
                    EncoderProfile = EncodingProfile.Describe(file),

                    ArtWidth       = artwork.Width,
                    ArtHeight      = artwork.Height,
                    ArtHash        = artwork.IsNone ? null : artwork.Hash,

                    Path = file
                };
            }
            catch (Exception ex)
            {
                // Debug, not Warning - a handful of unreadable/DRM'd/corrupt
                // files scattered through a large real library is routine,
                // not something worth a warning per file, but still worth
                // being able to find in the log when "why isn't track X
                // showing up" comes up.
                _logger.LogTrace(ex, "Skipping unreadable file during import: {Path}", file);
                return null;
            }
        }

        // An MP3's ID3v2.3 tag, whose name fields TagLib splits on every "/":
        // v2.3 has no separator of its own, and "/" was the convention for
        // several artists in one field. So "Akio / Okihide" read as two
        // artists, "Akio " and " Okihide", and joined with ", " became
        // "Akio ,  Okihide" - and a band called "yes/and" became "yes, and".
        // Wrong in every list, and a name Music.app, which shows the field as
        // written, never matched. ID3v2.4 separates with a NUL and leaves "/"
        // alone; every other format has real multiple values.
        private static TagLib.Id3v2.Tag? Id3v23(TagLib.File file) =>
            file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag { Version: < 4 } id3 ? id3 : null;

        // A name frame of a v2.3 tag as the file holds it: the pieces put back
        // together with the "/" they were split on, which restores the text
        // exactly - the split keeps the spaces either side, and an empty piece
        // between two slashes ("SAINT PEPSI // LUXURY ELITE"). Read from the
        // frame rather than from the Tag, whose Conductor and RemixedBy have
        // already been rejoined with "; ". Null when the file has no v2.3 tag
        // or no such frame, so the caller reads the Tag as before - a name an
        // ID3v1 or APE tag supplies is not one v2.3 split. A v2.3 file that
        // really does list "Artist A/Artist B" now shows exactly that: it is
        // what the file says, and cannot be told apart from a band with a
        // slash in its name.
        private static string? SlashSeparated(TagLib.Id3v2.Tag? id3v23, string frameId)
        {
            if (id3v23 == null)
                return null;

            var frame = TagLib.Id3v2.TextInformationFrame.Get(
                id3v23, TagLib.ByteVector.FromString(frameId, TagLib.StringType.Latin1), create: false);
            if (frame?.Text is not { Length: > 0 } pieces)
                return null;

            var joined = string.Join("/", pieces);
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        // The multi-valued sort tags (TSOP/TSOC) joined the same way Performers
        // and Composers themselves are, except that an absent one has to stay
        // null rather than become "": these are overrides, and an empty-string
        // override is indistinguishable from a real one that sorts everything
        // to the top (see Track.SortAs). Artists/Composers above can afford
        // string.Join because they are the displayed value, not an override.
        private static string? JoinOrNull(string[]? values)
        {
            if (values == null || values.Length == 0)
                return null;

            var joined = string.Join(", ", values);
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        // Reads the media folder Apple Music is configured to use, straight from its
        // preferences plist. Public so it can also be used to auto-populate the
        // configured library paths (see AppSettingsStore) rather than only as a silent
        // fallback when nothing is configured - called from there before any Importer
        // instance necessarily exists, so this takes an explicit logger from whichever
        // caller already has one (AppSettingsStore's own _logger) rather than reaching
        // for a static/global one.
        public static string? TryResolveAppleMusicFolder(ILogger? logger = null)
        {
            if (!OperatingSystem.IsMacOS())
                return null;

            try
            {
                var plistPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Preferences", "com.apple.Music.plist");

                if (File.Exists(plistPath) &&
                    PropertyListParser.Parse(plistPath) is NSDictionary root &&
                    root.TryGetValue("media-folder-url", out var locationNode) &&
                    Uri.TryCreate(locationNode.ToString(), UriKind.Absolute, out var mediaFolderUri))
                {
                    var mediaFolder = mediaFolderUri.LocalPath;
                    if (!string.IsNullOrEmpty(mediaFolder) && Directory.Exists(mediaFolder))
                        return mediaFolder;
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Could not read Apple Music's configured media folder from its preferences plist");
            }

            return null;
        }
    }
}
