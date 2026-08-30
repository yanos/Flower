using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Importer;
using Flower.Logging;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;

namespace Flower.Tests;

// A client pushes only the log lines its paired server says it is missing.
//
// The server is asked outright (GET /log/watermark, and the same shape back
// from every POST /log/report) rather than the client guessing from its own
// send history, because the client's history is the least reliable thing in the
// exchange: a phone restarts constantly, and a mark it kept in memory says
// nothing about what actually landed. What it keeps instead is a week of its
// own logs on disk (DeviceLogArchive), so "what you are missing" can be
// answered even for a day the server spent switched off - where the old
// 2000-entry memory ring could only ever offer whatever one process had left.
//
// Pinned to an isolated PlatformDataDirectory for the same reason
// LibrarySyncConditionalPullTests is: the sync path writes a real library.json.
[Collection("PlatformDataDirectory")]
public class LibrarySyncLogPushTests : IDisposable
{
    private const string LogReportPath = "/api/flower/v1/log/report";
    private const string LogWatermarkPath = "/api/flower/v1/log/watermark";

    private readonly string? _originalHome;
    private readonly string _tempHome;

    public LibrarySyncLogPushTests()
    {
        _originalHome = Environment.GetEnvironmentVariable("HOME");
        _tempHome = Path.Combine(Path.GetTempPath(), "flower-logpush-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempHome);
        Environment.SetEnvironmentVariable("HOME", _tempHome);
        PlatformDataDirectory.Current = _tempHome;
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _originalHome);
        PlatformDataDirectory.Current = AssemblySetup.DefaultDataDirectory;
        try { Directory.Delete(_tempHome, recursive: true); } catch { /* best effort */ }
    }

    // One archive per service, over this test's own temp home, so a case can
    // restart "the client" against the same on-disk week.
    private LibrarySyncService MakeService(DeviceSigningKey key) =>
        new(new Library([]),
            new DeviceIdentity { Fingerprint = key.Fingerprint, Alias = "Client" },
            key,
            new AppSettings { ShareLogsWithPairedServer = true },
            new DeviceLogArchive(new ClientLogStore(Path.Combine(_tempHome, "logs", "devices")), InMemoryLogStore.Instance),
            NullLogger<LibrarySyncService>.Instance,
            NullLogger<RemoteLibraryImporter>.Instance);

    // InMemoryLogStore.Instance is a process-wide singleton shared with every
    // other test in the run, so a report's entry list contains far more than
    // what this test logged. Counting a unique marker is the only stable
    // assertion available - see InMemoryLogStoreTests' own comment.
    private static string LogMarker()
    {
        var marker = Guid.NewGuid().ToString();
        InMemoryLogStore.Instance.Add(new InMemoryLogEntry(
            DateTimeOffset.Now, "Information", "Test", marker, null));
        return marker;
    }

    private static int CountMarker(IEnumerable<LogReportDto> reports, string marker)
    {
        var seen = 0;
        foreach (var report in reports)
            foreach (var entry in report.Entries)
                if (entry.Message == marker)
                    seen++;
        return seen;
    }

    // Stands in for the real server's half of the handshake: it accumulates
    // what it is sent, deduplicated by the same ClientLogStore.EventId the
    // server uses, and answers both endpoints with the newest entry it holds.
    private sealed class Peer : IDisposable
    {
        private readonly object _lock = new();
        private readonly List<LogEntryDto> _held = [];

        public required FakePeerHttpServer Server { get; init; }
        public List<LogReportDto> Reports { get; } = [];

        public void Accept(LogReportDto report)
        {
            lock (_lock)
            {
                Reports.Add(report);
                var known = _held.Select(ClientLogStore.EventId).ToHashSet(StringComparer.Ordinal);
                _held.AddRange(report.Entries.Where(entry => known.Add(ClientLogStore.EventId(entry))));
            }
        }

        // Anything the server has that the client does not know it has - the
        // case a client-side send history could never account for.
        public void Preload(IEnumerable<LogEntryDto> entries)
        {
            lock (_lock)
                _held.AddRange(entries);
        }

        public string Watermark()
        {
            lock (_lock)
            {
                var ordered = ClientLogStore.Ordered(_held);
                var watermark = ordered.Count == 0
                    ? new LogWatermarkDto(null, null)
                    : DeviceLogArchive.Watermark(ordered[^1]);
                return JsonSerializer.Serialize(watermark, FlowerJsonContext.Default.LogWatermarkDto);
            }
        }

        public void Dispose() => Server.Dispose();
    }

    private static Peer StartPeer(Func<int, HttpStatusCode> statusForRequest)
    {
        Peer? peer = null;
        var requests = 0;
        var server = new FakePeerHttpServer(async context =>
        {
            var path = context.Request.Url?.AbsolutePath;
            if (path == LogWatermarkPath)
            {
                await WriteJson(context, HttpStatusCode.OK, peer!.Watermark());
                return;
            }

            if (path != LogReportPath)
            {
                context.Response.Close();
                return;
            }

            using var body = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var json = await body.ReadToEndAsync();
            var status = statusForRequest(requests++);
            if (status != HttpStatusCode.OK)
            {
                context.Response.StatusCode = (int)status;
                context.Response.Close();
                return;
            }

            var report = JsonSerializer.Deserialize(json, FlowerJsonContext.Default.LogReportDto);
            if (report != null)
                peer!.Accept(report);

            // The ack: what the server holds now, which is what the client
            // resumes from next time.
            await WriteJson(context, HttpStatusCode.OK, peer!.Watermark());
        });
        return peer = new Peer { Server = server };
    }

    private static async Task WriteJson(HttpListenerContext context, HttpStatusCode status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static DiscoveredDevice DeviceFor(FakePeerHttpServer peer) => new()
    {
        InstanceName = "peer",
        BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, peer.Port)),
        Fingerprint = "peer-fingerprint",
    };

    private static Task<bool> PushAsync(LibrarySyncService service, DiscoveredDevice device)
    {
        // Archiving is a separate tick in the app (PeerSyncCoordinator), so a
        // test that wants a line pushed has to run both halves.
        return ArchiveThenPush(service, device);

        static async Task<bool> ArchiveThenPush(LibrarySyncService service, DiscoveredDevice device)
        {
            await service.ArchiveOwnLogsAsync();
            return await service.PushLogsOnlyAsync(device);
        }
    }

    [Fact]
    public async Task A_second_push_sends_only_the_lines_logged_since_the_first()
    {
        using var peer = StartPeer(_ => HttpStatusCode.OK);
        using var key = TestSigningKey.Create();
        var service = MakeService(key);
        var device = DeviceFor(peer.Server);

        var first = LogMarker();
        Assert.True(await PushAsync(service, device));

        var second = LogMarker();
        Assert.True(await PushAsync(service, device));

        // The first marker travels exactly once even though it is still in the
        // archive - and stays in it for a week - when the second push goes out.
        Assert.Equal(1, CountMarker(peer.Reports, first));
        Assert.Equal(1, CountMarker(peer.Reports, second));
    }

    [Fact]
    public async Task A_push_with_nothing_new_makes_no_request_at_all()
    {
        using var peer = StartPeer(_ => HttpStatusCode.OK);
        using var key = TestSigningKey.Create();
        var service = MakeService(key);
        var device = DeviceFor(peer.Server);

        LogMarker();
        Assert.True(await PushAsync(service, device));
        var afterFirst = peer.Reports.Count;

        // Reported as success: everything there is to send has been sent.
        Assert.True(await PushAsync(service, device));

        Assert.Equal(afterFirst, peer.Reports.Count);
    }

    // The watermark may only move on a 2xx. Advancing it on a failed POST would
    // leave the server permanently short of those lines - it has no way to ask
    // for them again, since asking is the client's job.
    [Fact]
    public async Task A_failed_push_resends_the_same_lines_next_time()
    {
        using var peer = StartPeer(request => request == 0 ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
        using var key = TestSigningKey.Create();
        var service = MakeService(key);
        var device = DeviceFor(peer.Server);

        var marker = LogMarker();
        Assert.False(await PushAsync(service, device));
        Assert.Empty(peer.Reports);

        Assert.True(await PushAsync(service, device));
        Assert.Equal(1, CountMarker(peer.Reports, marker));
    }

    // The point of asking rather than remembering: a restarted client knows
    // nothing about what it sent last time, and must not replay its whole
    // retained week at a server that already has it.
    [Fact]
    public async Task A_restarted_client_resumes_from_what_the_server_says_it_holds()
    {
        using var peer = StartPeer(_ => HttpStatusCode.OK);
        using var key = TestSigningKey.Create();
        var device = DeviceFor(peer.Server);

        var delivered = LogMarker();
        Assert.True(await PushAsync(MakeService(key), device));

        var afterFirst = peer.Reports.Count;
        var fresh = LogMarker();

        // A brand new service over the same on-disk archive: same client, next
        // launch. It still holds the delivered line and must not re-send it.
        Assert.True(await PushAsync(MakeService(key), device));

        Assert.Equal(afterFirst + 1, peer.Reports.Count);
        Assert.Equal(1, CountMarker(peer.Reports, delivered));
        Assert.Equal(1, CountMarker(peer.Reports, fresh));
    }

    // And the other direction: a server holding lines this client never sent it
    // (an earlier install, a restored backup) is not asked to take them again.
    [Fact]
    public async Task Lines_the_server_already_holds_are_not_offered_again()
    {
        using var peer = StartPeer(_ => HttpStatusCode.OK);
        using var key = TestSigningKey.Create();
        var service = MakeService(key);
        var device = DeviceFor(peer.Server);

        var known = LogMarker();
        await service.ArchiveOwnLogsAsync();
        peer.Preload(InMemoryLogStore.Instance.Snapshot().Select(LogEntryDto.FromEntry));

        var fresh = LogMarker();
        Assert.True(await PushAsync(service, device));

        Assert.Equal(0, CountMarker(peer.Reports, known));
        Assert.Equal(1, CountMarker(peer.Reports, fresh));
    }
}
