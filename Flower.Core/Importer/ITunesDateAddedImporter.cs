using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Flower.Models;

namespace Flower.Importer;

// Applies Music.app dates added; snapshot acquisition and matching are shared.
public static class ITunesDateAddedImporter
{
    public static bool ApplyFromXmlFile(IEnumerable<Track> tracks, string xmlPath, ILogger? logger = null) =>
        ITunesSnapshot.FromXmlFile(xmlPath, logger) is { } snapshot && Apply(tracks, snapshot, logger);

    public static bool Apply(IEnumerable<Track> tracks, ITunesSnapshot snapshot, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var matchedCount = 0;
        foreach (var track in tracks)
        {
            var candidate = snapshot.Dates.Find(track);

            if (candidate is { } value && value < track.DateAdded)
            {
                track.DateAdded = value;
                matchedCount++;
            }
        }

        logger.LogInformation("iTunes date added sync updated {MatchedCount} of {ExportedCount} exported tracks to an older date", matchedCount, snapshot.Dates.Count);
        return matchedCount > 0;
    }
}
