using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Models;
using Flower.Services;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests;

// The browser head's play reporting, against a real socket - the same level
// RemoteLibraryImporterTests works at, and for the same reason: a tab has no
// LibrarySyncService, so this class is the whole of what stands between a play
// happening in a tab and the origin server counting it.
public class OriginPlayReporterTests
{
    private static readonly HttpClient Http = new();

    private static OriginPlayReporter Reporter(FakePeerHttpServer origin) =>
        new(Http, $"http://127.0.0.1:{origin.Port}", new UnauthenticatedCredentials(),
            NullLogger<OriginPlayReporter>.Instance);

    private static Track Placeholder(string originTrackId = "tr-1") =>
        new() { Title = "On A Server", Path = null, OriginTrackId = originTrackId };

    // Collects the batches the reporter posts, answering however the caller
    // says. Bodies are captured under a lock: the pump posts from a threadpool
    // task while the test reads.
    private sealed class RecordingOrigin : IDisposable
    {
        private readonly List<PlayReportDto> _batches = new();

        public FakePeerHttpServer Server { get; }

        public int Failures { get; set; }

        public RecordingOrigin()
        {
            Server = new FakePeerHttpServer(async context =>
            {
                using var reader = new System.IO.StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();

                bool reject;
                lock (_batches)
                {
                    // Recorded either way, so a test can see what a *rejected*
                    // attempt carried and compare it against the retry.
                    _batches.Add(JsonSerializer.Deserialize<PlayReportDto>(
                        body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

                    reject = Failures > 0;
                    if (reject)
                        Failures--;
                }

                context.Response.StatusCode = reject
                    ? (int)HttpStatusCode.ServiceUnavailable
                    : (int)HttpStatusCode.NoContent;
                context.Response.Close();
            });
        }

        public List<PlayReportDto> Batches
        {
            get
            {
                lock (_batches)
                    return new List<PlayReportDto>(_batches);
            }
        }

        public void Dispose() => Server.Dispose();
    }

    [Fact]
    public async Task A_finished_play_reaches_the_origin_as_a_completion()
    {
        using var origin = new RecordingOrigin();
        var reporter = Reporter(origin.Server);

        reporter.Report(Placeholder(), TrackStatsChange.Finished);
        await reporter.InFlight;

        var play = Assert.Single(Assert.Single(origin.Batches).Plays);
        Assert.Equal("tr-1", play.TrackId);
        Assert.True(play.Completed);
        Assert.False(play.Started);
    }

    // The two halves stay distinct on the wire, so the far side ends up with
    // the History a local player would have had rather than counting a skip as
    // a listen - see Library.TrackStatsChange.
    [Fact]
    public async Task A_started_play_is_reported_as_a_start_and_not_as_a_count()
    {
        using var origin = new RecordingOrigin();
        var reporter = Reporter(origin.Server);

        reporter.Report(Placeholder(), TrackStatsChange.Started);
        await reporter.InFlight;

        var play = Assert.Single(Assert.Single(origin.Batches).Plays);
        Assert.True(play.Started);
        Assert.False(play.Completed);
    }

    // A desktop's own file, or a track imported from somewhere that is not this
    // server. There is no id this server would recognise, and inventing one
    // would count the play against whatever track happened to collide.
    [Fact]
    public async Task A_track_with_no_origin_id_is_not_reported_at_all()
    {
        using var origin = new RecordingOrigin();
        var reporter = Reporter(origin.Server);

        reporter.Report(new Track { Title = "Local", Path = "/music/a.mp3" }, TrackStatsChange.Finished);
        await reporter.InFlight;

        Assert.Empty(origin.Batches);
    }

    // The difference from OriginPlaylistWriter, which deliberately does not
    // retry: a rejected playlist edit is still on screen to make again, and a
    // swallowed play is simply gone. The backlog leaves with the next play.
    [Fact]
    public async Task A_batch_that_failed_to_send_goes_out_with_the_next_one()
    {
        using var origin = new RecordingOrigin { Failures = 1 };
        var reporter = Reporter(origin.Server);

        reporter.Report(Placeholder("tr-first"), TrackStatsChange.Finished);
        await reporter.InFlight;

        reporter.Report(Placeholder("tr-second"), TrackStatsChange.Finished);
        await reporter.InFlight;

        // Two attempts: the rejected one, then the retry carrying both. In the
        // order they were played, not the order they were sent.
        Assert.Equal(2, origin.Batches.Count);
        Assert.Equal(["tr-first", "tr-second"], origin.Batches[1].Plays.ConvertAll(p => p.TrackId));
    }

    // What the server's dedupe is keyed on. A retry re-sends the same events
    // verbatim, so the ids have to survive the round trip unchanged - minting
    // fresh ones on the way back out would make the retry indistinguishable
    // from two real plays.
    [Fact]
    public async Task A_resent_batch_carries_the_same_event_ids()
    {
        using var origin = new RecordingOrigin { Failures = 1 };
        var reporter = Reporter(origin.Server);

        reporter.Report(Placeholder(), TrackStatsChange.Finished);
        await reporter.InFlight;

        reporter.Report(Placeholder(), TrackStatsChange.Finished);
        await reporter.InFlight;

        var rejected = origin.Batches[0].Plays;
        var retried = origin.Batches[1].Plays;
        Assert.Equal(rejected[0].EventId, retried[0].EventId);

        // And each real play still gets an id of its own, so the server's
        // dedupe drops the repeat without also dropping the second track.
        Assert.NotEqual(retried[0].EventId, retried[1].EventId);
    }

    private sealed class UnauthenticatedCredentials : IPeerCredentials
    {
        public IEnumerable<(string Key, string Value)> Authorize(
            string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) => [];
    }
}
