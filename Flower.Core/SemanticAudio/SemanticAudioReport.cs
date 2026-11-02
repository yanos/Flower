using System;
using System.Collections.Generic;
using System.Linq;

using Flower.Models;
using Flower.Services;

namespace Flower.SemanticAudio;

// The engine's state as the settings page and a paired device are told it
// (VibeStatusDto, SyncInfoResponseDto.VibePlaylists): on a server for its
// admin API and /info, and in-process on a device on its own, so both say
// the same thing the same way.
public sealed class SemanticAudioReport(SemanticAudioEngine engine, Library library)
{
    private readonly object _costsGate = new();
    private (LibrarySnapshot Snapshot, double? Rate, IReadOnlyList<SemanticAudioCost> Costs)? _costs;

    public VibeStatusDto Describe()
    {
        var status = engine.Status;
        var analysis = status.Analysis;
        var storage = engine.Storage();
        var byId = library.Snapshot.ById;

        var failures = engine.Failures()
            .Select(f => byId.GetValueOrDefault(f.TrackId) is { } track ? new VibeFailureDto(track.Title ?? "", track.Artists ?? "", f.Reason) : null)
            .OfType<VibeFailureDto>()
            .ToList();

        var analysing = status.State == SemanticAudioState.Analysing;
        double? secondsLeft = analysing && analysis.SongsPerSecond is > 0
            ? Math.Max(0, analysis.Eligible - analysis.Done) / analysis.SongsPerSecond.Value
            : null;

        return new VibeStatusDto(
            status.State.ToString(),
            status.Download is { } download ? new VibeDownloadDto(download.BytesDone, download.BytesTotal) : null,
            analysis.Done,
            analysis.Eligible,
            status.SongsSearchable,
            analysing ? analysis.CurrentTitle : null,
            analysing ? analysis.CurrentArtist : null,
            secondsLeft,
            status.Error,
            storage.ModelBytes,
            storage.AnalysisBytes,
            Costs(analysis.ClipsPerSecond)
                .Select(c => new VibeClipCostDto(c.ClipsPerSong, c.Clips, c.Bytes, c.Time?.TotalSeconds))
                .ToList(),
            failures.Count,
            failures.Take(VibeStatusDto.FailureListLimit).ToList());
    }

    // What /info tells a paired device: whether there is anything to make a
    // Vibe Playlist from.
    public (string State, int? Percent) DescribeForInfo()
    {
        if (!engine.Enabled)
            return ("off", null);

        var status = engine.Status;
        switch (status.State)
        {
            case SemanticAudioState.UpToDate:
                return ("ready", null);
            case SemanticAudioState.Failed:
                // What was heard before the error still searches.
                return status.SongsSearchable > 0 ? ("ready", null) : ("off", null);
            default:
                var analysis = status.Analysis;
                return ("analysing", analysis.Eligible > 0 ? (int)(100L * analysis.Done / analysis.Eligible) : 0);
        }
    }

    // Counted over the whole library, so kept until the library or the
    // measured rate changes rather than redone on every two-second poll.
    private IReadOnlyList<SemanticAudioCost> Costs(double? clipsPerSecond)
    {
        var snapshot = library.Snapshot;
        lock (_costsGate)
        {
            if (_costs is { } kept && ReferenceEquals(kept.Snapshot, snapshot) && kept.Rate == clipsPerSecond)
                return kept.Costs;

            var costs = SemanticAudioCosts.For(snapshot.Tracks, clipsPerSecond);
            _costs = (snapshot, clipsPerSecond, costs);
            return costs;
        }
    }
}
