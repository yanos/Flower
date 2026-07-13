using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Claunia.PropertyList;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Models;

namespace Flower.Importer
{
    public class Importer : IMusicImporter
    {
        // Constructed once, ad-hoc, at App.axaml.cs's composition root, with no
        // other call sites - AppLogging.CreateLogger<T>() rather than a
        // constructor parameter, same reasoning as AlbumArtLoader.
        private static readonly ILogger Logger = AppLogging.CreateLogger<Importer>();

        private readonly HashSet<string> _validExtensions = [".mp3", ".m4a", ".wav", ".flac", ".alac"];

        public Importer() { }

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
            var paths = configured is { Count: > 0 } ? configured : new List<string> { ResolveMusicPath() };

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
                    Logger.LogDebug(ex, "Skipping unreadable file during import: {Path}", file);
                }
            }
        }

        private static string ResolveMusicPath()
        {
            // iOS has no shared Music folder; the app's sandboxed Documents directory
            // (populated via Finder file sharing, see Info.plist UIFileSharingEnabled)
            // is the only place it can read files from.
            if (OperatingSystem.IsIOS())
                return Environment.GetFolderPath(Environment.SpecialFolder.Personal);

            return TryResolveAppleMusicFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }

        // Reads the media folder Apple Music is configured to use, straight from its
        // preferences plist. Public so it can also be used to auto-populate the
        // configured library paths (see AppSettingsStore) rather than only as a silent
        // fallback when nothing is configured.
        public static string? TryResolveAppleMusicFolder()
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
                Logger.LogDebug(ex, "Could not read Apple Music's configured media folder from its preferences plist");
            }

            return null;
        }
    }
}
