using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Claunia.PropertyList;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Importer
{
    public class Importer : IMusicImporter
    {
        private readonly ILogger<Importer> _logger;
        private readonly HashSet<string> _validExtensions = [".mp3", ".m4a", ".wav", ".flac", ".alac"];

        public Importer(ILogger<Importer> logger)
        {
            _logger = logger;
        }

        public Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null)
            => Task.Run(() => Import(libraryPaths));

        public List<Track> Import(IEnumerable<string>? libraryPaths = null)
        {
            var tracks = new List<Track>();
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            List<string> paths;
            if (configured is { Count: > 0 })
                paths = configured;
            else if (OperatingSystem.IsIOS())
                paths = [Environment.GetFolderPath(Environment.SpecialFolder.Personal)];
            else
            {
                _logger.LogInformation("No library folders configured - nothing to scan");
                return tracks;
            }

            foreach (var path in paths)
            {
                ImportFrom(path, tracks, seenFiles);
            }

            return tracks;
        }

        private void ImportFrom(string path, List<Track> tracks, HashSet<string> seenFiles)
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => _validExtensions.Contains(Path.GetExtension(f).ToLower()));

            foreach (var file in files)
            {
                // Skip files already imported from an overlapping configured path.
                if (!seenFiles.Add(file))
                    continue;

                try
                {
                    var tagFile = TagLib.File.Create(file);
                    var tag = tagFile.Tag;
                    var props = tagFile.Properties;

                    var codec = props?.Codecs != null
                        ? string.Join(", ", props.Codecs.Where(c => c != null).Select(c => c.Description).Where(d => !string.IsNullOrEmpty(d)))
                        : null;

                    tracks.Add(new Track
                    {
                        // Core identity
                        Title         = tag.Title,
                        Subtitle      = tag.Subtitle,
                        Artists       = string.Join(", ", tag.Performers),
                        AlbumArtists  = string.Join(", ", tag.AlbumArtists),
                        IsCompilation = ReadIsCompilation(tagFile),
                        Album         = tag.Album,
                        AlbumSort     = tag.AlbumSort,
                        Year          = tag.Year > 0 ? tag.Year.ToString() : null,
                        TrackNumber   = tag.Track,
                        TrackCount    = tag.TrackCount,
                        DiscNumber    = tag.Disc,
                        DiscCount     = tag.DiscCount,

                        // People
                        Composers     = string.Join(", ", tag.Composers),
                        Conductor     = tag.Conductor,
                        RemixedBy     = tag.RemixedBy,

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
                        Bitrate        = props?.AudioBitrate ?? 0,
                        SampleRate     = props?.AudioSampleRate ?? 0,
                        Channels       = props?.AudioChannels ?? 0,
                        BitsPerSample  = props?.BitsPerSample ?? 0,
                        Codec          = codec,

                        Path = file
                    });
                }
                catch (Exception ex)
                {
                    // Debug, not Warning - a handful of unreadable/DRM'd/corrupt
                    // files scattered through a large real library is routine,
                    // not something worth a warning per file, but still worth
                    // being able to find in the log when "why isn't track X
                    // showing up" comes up.
                    _logger.LogTrace(ex, "Skipping unreadable file during import: {Path}", file);
                }
            }
        }

        // The "part of a compilation" flag (ID3 TCMP, MP4 cpil, Xiph COMPILATION)
        // lives on each concrete tag format's own type, not the generic TagLib.Tag
        // the rest of this importer reads through - so unlike every other field
        // above, this can't be read off the plain tag.* API and needs its own
        // per-format lookup. Covers every format Importer actually scans (mp3 via
        // Id3v2, m4a/alac via Apple/Mpeg4, flac via Xiph); wav has no equivalent
        // tag type in TagLib# and is left false. See Track.IsCompilation/
        // EffectiveAlbumArtist for why this matters: it is the one reliable,
        // non-heuristic signal that a various-artists album should be grouped as
        // a single tile instead of fragmenting by each track's own Artists.
        private static bool ReadIsCompilation(TagLib.File tagFile) =>
            tagFile.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3v2 && id3v2.IsCompilation
            || tagFile.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple && apple.IsCompilation
            || tagFile.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph && xiph.IsCompilation;

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
