using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Flower.Models;

namespace Flower.Importer
{
    public class Importer
    {
        private readonly HashSet<string> _validExtensions = [".mp3", ".m4a", ".wav", ".flac", ".alac"];

        public Importer() { }

        public List<Track> Import()
        {
            var tracks = new List<Track>();

            var path = "/Users/yanos/Music";

            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Where(f => _validExtensions.Contains(Path.GetExtension(f).ToLower()));

            foreach (var file in files)
            {
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
                catch { /* skip unreadable files */ }
            }

            return tracks;
        }
    }
}
