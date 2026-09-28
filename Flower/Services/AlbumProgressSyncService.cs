using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;

namespace Flower.Services;

// Keeps this device's Continue Playing shelf in step with the listener's other
// devices, through the server - see AlbumProgressProtocol for the exchange
// and whose shelf the server keeps for whom.
//
// Exchanges when the shelf changes here (a song starting, a pause, an album
// finished or removed), when Home is opened, and every half minute besides,
// which is how a move made on another device arrives. There is no cheaper
// "has anything changed" to ask first: the exchange is one small request, and
// on the bulk budget two a minute is noise beside what browsing spends.
//
// Every head has one, pointed at wherever its server is: the paired server
// while it is reachable on a desktop or phone, the origin on a browser tab -
// where it is also the only thing that keeps the shelf across a refresh,
// since a tab's own files live in memory.
//
// The shelf is translated at the door. Locally an album's place is a Track.Id,
// which means nothing to the server; on the wire it is the server's id for
// the song (Track.OriginTrackId). A song this device has no server id for - a
// file of its own - is not the server's to hear about, and an album the
// server names by a song this device does not have stays on the server,
// waiting for a device that does.
public sealed class AlbumProgressSyncService : IDisposable
{
    public static readonly TimeSpan PullInterval = TimeSpan.FromSeconds(30);

    // Long enough for a burst - a song ending and the next starting, both of
    // which change the shelf - to go out as one exchange.
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);

    private readonly Library _library;
    private readonly AlbumProgressTracker _tracker;
    private readonly IPeerCredentials _credentials;
    private readonly HttpClient _http;
    private readonly Func<Uri?> _server;
    private readonly ILogger<AlbumProgressSyncService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _exchanging = new(1, 1);
    private int _scheduled;

    // server: the server's base address right now, or null while there is
    // none to talk to - asked afresh on every exchange, since a paired server
    // comes and goes and moves between addresses.
    public AlbumProgressSyncService(
        Library library,
        AlbumProgressTracker tracker,
        IPeerCredentials credentials,
        HttpClient http,
        Func<Uri?> server,
        ILogger<AlbumProgressSyncService> logger,
        bool runPeriodically = true)
    {
        _library = library;
        _tracker = tracker;
        _credentials = credentials;
        _http = http;
        _server = server;
        _logger = logger;

        _tracker.LocallyChanged += OnLocallyChanged;
        if (runPeriodically)
            _ = PullPeriodicallyAsync(_stopping.Token);
    }

    public void Dispose()
    {
        _tracker.LocallyChanged -= OnLocallyChanged;
        _stopping.Cancel();
    }

    private void OnLocallyChanged(object? sender, EventArgs e) => ExchangeSoon();

    private void ExchangeSoon()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, _stopping.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Interlocked.Exchange(ref _scheduled, 0);
            await ExchangeNowAsync();
        });
    }

    private async Task PullPeriodicallyAsync(CancellationToken stopping)
    {
        try
        {
            // Once straight away, so a device opened after another one was
            // used picks up where that one left off before anyone asks.
            await ExchangeNowAsync();
            while (true)
            {
                await Task.Delay(PullInterval, stopping);
                await ExchangeNowAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // One exchange, now. Safe to call from anywhere; one that arrives while
    // another is in flight waits for it and then goes, so what it carries is
    // never older than the change that asked for it. False when there was no
    // server or it could not be reached - nothing is lost either way, since
    // the shelf is all still here to be sent next time.
    public async Task<bool> ExchangeNowAsync()
    {
        if (_server() is not { } server)
            return false;

        await _exchanging.WaitAsync();
        try
        {
            var outgoing = Outgoing();
            var body = JsonSerializer.SerializeToUtf8Bytes(
                new AlbumProgressExchangeDto(outgoing), PlayReportJsonContext.Default.AlbumProgressExchangeDto);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, AlbumProgressProtocol.Path));
            await request.AddPeerCredentialsAsync(_credentials, body);
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;

            using var response = await _http.SendAsync(request, _stopping.Token);
            response.EnsureSuccessStatusCode();

            var answer = await JsonSerializer.DeserializeAsync(
                await response.Content.ReadAsStreamAsync(_stopping.Token),
                PlayReportJsonContext.Default.AlbumProgressExchangeDto,
                _stopping.Token);
            if (answer?.Albums is { } albums)
                Apply(albums);

            _logger.LogDebug("Exchanged album progress: {Sent} sent, {Received} received",
                outgoing.Count, answer?.Albums?.Count ?? 0);
            return true;
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            // Debug, not Warning, for the reason LibrarySyncService gives about
            // its own pushes: this runs on a timer against a server that is
            // often simply not there.
            _logger.LogDebug(ex, "Could not exchange album progress with {Server}", server);
            return false;
        }
        finally
        {
            _exchanging.Release();
        }
    }

    internal List<AlbumProgressDto> Outgoing()
    {
        var byId = new Dictionary<Guid, Track>();
        foreach (var track in _library.Tracks)
            byId.TryAdd(track.Id, track);

        var outgoing = new List<AlbumProgressDto>();
        foreach (var entry in _tracker.Entries)
        {
            if (byId.TryGetValue(entry.TrackId, out var track) && track.OriginTrackId is { Length: > 0 } originTrackId)
                outgoing.Add(new AlbumProgressDto(entry.AlbumId, originTrackId, entry.Position.TotalSeconds, entry.UpdatedAt));
        }

        foreach (var removal in _tracker.Removals)
            outgoing.Add(new AlbumProgressDto(removal.AlbumId, null, 0, removal.RemovedAt));

        return outgoing;
    }

    internal void Apply(IReadOnlyList<AlbumProgressDto> albums)
    {
        var byOriginId = new Dictionary<string, Track>();
        foreach (var track in _library.Tracks)
        {
            if (track.OriginTrackId is { Length: > 0 } originTrackId)
                byOriginId.TryAdd(originTrackId, track);
        }

        var entries = new List<AlbumProgressEntry>();
        var removals = new List<AlbumProgressRemoval>();
        foreach (var album in albums)
        {
            if (album.IsRemoval)
                removals.Add(new AlbumProgressRemoval(album.AlbumId, album.UpdatedAt));
            else if (byOriginId.TryGetValue(album.TrackId!, out var track))
                entries.Add(new AlbumProgressEntry(
                    album.AlbumId, track.Id, TimeSpan.FromSeconds(Math.Max(0, album.PositionSeconds)), album.UpdatedAt));
        }

        _tracker.MergeRemote(entries, removals);
    }
}
