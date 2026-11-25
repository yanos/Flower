using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Sync;

// A rescan the owner started on their server, followed into this app's status
// bar until the server says it is over.
public class ServerRescanWatchTests
{
    private static AdminLibraryStatusDto Scanning(int? done, int? total) =>
        new(Rescanning: true, TrackCount: 0, LastCompletedAt: null, LastError: null, UnwritableFolders: [], SongsScanned: done, SongsToScan: total);

    private static readonly AdminLibraryStatusDto Idle =
        new(Rescanning: false, TrackCount: 0, LastCompletedAt: null, LastError: null, UnwritableFolders: [], SongsScanned: null, SongsToScan: null);

    // Answers in turn, the last one for every call after.
    private static Func<CancellationToken, Task<AdminLibraryStatusDto>> Answers(params AdminLibraryStatusDto[] answers)
    {
        var next = 0;
        return _ => Task.FromResult(answers[Math.Min(next++, answers.Length - 1)]);
    }

    [Fact]
    public async Task Counts_the_servers_scan_until_it_ends()
    {
        var scopes = new List<RecordingBusyScope>();
        var watch = new ServerRescanWatch(
            Answers(Scanning(null, null), Scanning(0, 300), Scanning(120, 300), Idle),
            message => { var scope = new RecordingBusyScope(message); scopes.Add(scope); return scope; },
            "Scanning lambda",
            pollInterval: TimeSpan.Zero);

        await watch.FollowAsync(TestContext.Current.CancellationToken);

        var scope = Assert.Single(scopes);
        Assert.Equal("Scanning lambda…", scope.Opening);
        Assert.Equal(["Scanning lambda: 0 of 300 songs", "Scanning lambda: 120 of 300 songs"], scope.Said);
        Assert.True(scope.Disposed);
    }

    // Followed after every save, most of which start no scan at all: those
    // must not flash the indicator up and down.
    [Fact]
    public async Task Shows_nothing_when_the_server_is_not_scanning()
    {
        var scopes = 0;
        var watch = new ServerRescanWatch(
            Answers(Idle),
            _ => { scopes++; return new RecordingBusyScope(); },
            "Scanning lambda",
            pollInterval: TimeSpan.Zero);

        await watch.FollowAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, scopes);
    }

    // A server that stops answering mid-scan ends the following, and takes
    // the indicator down with it rather than leaving a count stuck up.
    [Fact]
    public async Task Stops_and_takes_the_indicator_down_when_the_server_stops_answering()
    {
        var calls = 0;
        RecordingBusyScope? scope = null;
        var watch = new ServerRescanWatch(
            _ => ++calls == 1 ? Task.FromResult(Scanning(5, 10)) : throw new HttpRequestException("gone"),
            _ => scope = new RecordingBusyScope(),
            "Scanning lambda",
            pollInterval: TimeSpan.Zero);

        await watch.FollowAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(scope);
        Assert.True(scope.Disposed);
    }
}
