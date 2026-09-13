using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// The chain a play actually travels on the browser head, wired exactly as
// App.axaml.cs wires it: playback moves a counter on Library, Library raises
// TrackStatsChanged saying which half moved, and the reporter forwards it to
// the origin server. OriginPlayReporterTests covers the far end of that (the
// HTTP), and SyncEndpointTests the server's; this covers the join, which is
// where "the play reached the reporter at all" is decided.
[Collection("PlatformDataDirectory")]
public class BrowserPlayReportingTests : PinnedDataDirectory
{
    private sealed class RecordingReporter : IPlayReporter
    {
        public List<(Track Track, TrackStatsChange Change)> Reported { get; } = new();

        public Task InFlight => Task.CompletedTask;

        public void Report(Track track, TrackStatsChange change) => Reported.Add((track, change));
    }

    private sealed class ImmediateStreamUrls : IStreamUrlResolver
    {
        public Task<string?> ResolveAsync(Track track) =>
            Task.FromResult<string?>($"http://origin.local/rest/stream?id={track.OriginTrackId}");
    }

    private static (PlaylistControlViewModel Playback, FakeAudioManager Audio, RecordingReporter Reporter, Track Placeholder)
        BrowserTab()
    {
        var placeholder = new Track { Title = "On A Server", Path = null, OriginTrackId = "tr-42" };
        var library = new Library(new List<Track> { placeholder });
        var reporter = new RecordingReporter();

        // The one line App.axaml.cs adds for a head that registers a reporter.
        library.TrackStatsChanged += (_, e) => reporter.Report(e.Track, e.Change);

        var audio = new FakeAudioManager();
        var playback = new PlaylistControlViewModel(
            audio, new MainPlaylist(new List<Track> { placeholder }), library, new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            NullLogger<PlaylistControlViewModel>.Instance,
            new ImmediateStreamUrls());

        // The play-count/resume-position writes are deliberately handed off
        // the LibVLC decode callback thread (see
        // PlaylistControlViewModel.OffPlaybackThread); run them inline so a
        // test asserting straight after RaiseEndReached() isn't racing the
        // pool - and so no stray pool item outlives the test.
        playback.OffPlaybackThread = work => work();

        return (playback, audio, reporter, placeholder);
    }

    // Starting a track is a History entry, not a listen. The server has to be
    // told the same thing, or a tab's History and the server's diverge in
    // content rather than merely in timing - a skipped track appears in one
    // and not the other.
    [Fact]
    public void Starting_a_streamed_track_reports_the_started_half()
    {
        var (playback, _, reporter, placeholder) = BrowserTab();

        playback.Play(placeholder);

        var (track, change) = Assert.Single(reporter.Reported);
        Assert.Equal(TrackStatsChange.Started, change);
        Assert.Equal("tr-42", track.OriginTrackId);
    }

    [Fact]
    public void A_track_that_ended_naturally_reports_the_finished_half()
    {
        var (playback, audio, reporter, placeholder) = BrowserTab();

        playback.Play(placeholder);
        reporter.Reported.Clear();
        audio.RaiseEndReached();

        var (track, change) = Assert.Single(reporter.Reported);
        Assert.Equal(TrackStatsChange.Finished, change);
        Assert.Equal("tr-42", track.OriginTrackId);
    }

    // The track handed to the reporter is the one in the library, not the
    // throwaway clone carrying the stream URL that playback actually fed to
    // the audio manager - see Library.ResolveCurrent. It matters here because
    // it is the same resolution that decides whether the tab's own row updates.
    [Fact]
    public void The_reported_track_is_the_librarys_own_and_its_counter_moved()
    {
        var (playback, audio, reporter, placeholder) = BrowserTab();

        playback.Play(placeholder);
        audio.RaiseEndReached();

        Assert.All(reporter.Reported, r => Assert.Same(placeholder, r.Track));
        Assert.Equal(1, placeholder.PlayCount);
        Assert.NotNull(placeholder.LastPlayedAt);
    }
}
