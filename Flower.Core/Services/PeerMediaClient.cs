using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Flower.Services;

// How a Flower client asks a Flower server for bytes: a signed URL for a track
// id, and a resumable download of one.
//
// It was OpenSubsonicClient, a whole hand-rolled browse client speaking /rest -
// getArtists, getAlbumList2, search3, playlist CRUD, star, scrobble, ping, all
// parsing the subsonic-response envelope. Every one of those lost its last
// caller when the catalog moved to Flower's own bulk routes, and what was left
// spoke a third party's protocol to a server this project also writes, for the
// two things that protocol was never the reason for. So it asks
// /api/flower/v1/stream and /download now, and is named after what it does.
//
// A genuine OpenSubsonic client is still the right thing for a third-party
// server (Navidrome, Jellyfin) with no bulk route, and would come back as its
// own class rather than as fields on this one. Nothing asks yet.
public class PeerMediaClient
{
    public const string StreamPath = "/api/flower/v1/stream";
    private const string DownloadPath = "/api/flower/v1/download";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly IPeerCredentials? _credentials;

    // credentials is the signed trust gate every route in this group requires
    // (X-Flower-Fingerprint/-Alias/-PublicKey/-Signature/-Timestamp/-Nonce -
    // see SignedDeviceCredentials). An object consulted per request rather than
    // a fixed header list, because a signature/nonce must be unique per call
    // (see DeviceSigningKey.Sign) and this client is long-lived: it signs once
    // per stream URL and once per download, so the identity params can never be
    // computed just once at construction time.
    //
    // Nullable only because a caller without a signing key is a real state (a
    // head that has never paired). Such a caller gets an unsigned URL, which the
    // server refuses - which is the correct outcome and a clearer one than
    // failing to build a URL at all.
    //
    // The u/t/s/v/c/f query params this used to attach went with /rest. They
    // were the classic Subsonic credential set, and Flower's own surface has
    // never accepted one: PeerMediaClientFactory always passed empty
    // strings for both, so every request carried a token over an empty password
    // that no gate on either end ever looked at.
    public PeerMediaClient(
        string baseUrl, HttpClient? httpClient = null, IPeerCredentials? credentials = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = httpClient ?? PeerHttpClient.Create();
        _credentials = credentials;
    }

    // Builds a URL with the peer-identity/signature credentials embedded in the
    // query string - necessary for a URL handed to something else to fetch (the
    // decoder opening a stream URL, or an <audio> element handed one), which
    // cannot carry the custom headers an authenticated HttpClient call can; see
    // SignedRequest.Identity, which accepts either. Not used by
    // DownloadTrackAsync below, which sends the identical information as
    // headers instead (see BuildPlainUrl).
    private async Task<string> BuildSignedUrlAsync(string path, List<(string Key, string Value)> parameters)
    {
        if (_credentials != null)
            parameters.AddRange(await _credentials.AuthorizeAsync("GET", path, parameters, []));

        return $"{_baseUrl}{path}?{Query(parameters)}";
    }

    // Counterpart to BuildSignedUrlAsync for DownloadTrackAsync: the URL
    // without peer identity baked into the query, because those travel as
    // headers instead, computed fresh per request (see
    // AddPeerIdentityHeadersAsync) - this avoids generating and discarding an
    // unused, individually-still-valid signed query string alongside every
    // header-authenticated call.
    private string BuildPlainUrl(string path, List<(string Key, string Value)> parameters) =>
        $"{_baseUrl}{path}?{Query(parameters)}";

    private static string Query(IEnumerable<(string Key, string Value)> parameters) =>
        string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    // Only forces a fresh connection per request (rather than pooling/reusing
    // one) when talking to a peer Flower device - a third-party server, which
    // has no credentials to add here, keeps whatever pooling it would have had.
    // A session against a peer is only ever a couple of requests, so the extra
    // handshake is negligible, and it
    // avoids reusing a keep-alive connection the peer's HttpListener (or the
    // OS, e.g. after iOS backgrounds the app) already tore down - observed in
    // practice as "Connection reset by peer" on iOS.
    private async Task AddPeerIdentityHeadersAsync(HttpRequestMessage request, string method, string path, IEnumerable<(string Key, string Value)> parameters)
    {
        if (_credentials == null)
            return;

        // Percent-encoded, same as every other header-transport call site -
        // see IdentityHeaderEncoding. BuildUrlAsync above sends the identical
        // params as query params and escapes them itself; this is the half
        // that would otherwise throw on a device whose alias has an accent in
        // it.
        foreach (var header in await _credentials.AuthorizeAsync(method, path, parameters, []))
            request.Headers.Add(header.Key, IdentityHeaderEncoding.Encode(header.Value));
        request.Headers.ConnectionClose = true;
    }

    // The caller streams the bytes itself (the decoder opens this URL directly),
    // so this builds a fully-authed URL rather than buffering audio into memory
    // here. See SYNC-PLAN.md Phase 3's download flow.
    //
    // A task, like everything else that signs: the browser's key answers through
    // crypto.subtle and cannot be asked on the calling stack (see
    // IPeerCredentials). Every other head completes this synchronously.
    public Task<string> GetStreamUrlAsync(string id) =>
        BuildSignedUrlAsync(StreamPath, [("id", id)]);

    // Streams /download?id=... straight to a file rather than buffering the
    // whole track in memory - see LibraryDownloadService (SYNC-PLAN.md Phase 3's
    // download button). Signs per request like every other call, so it goes
    // through the peer's trust gate the same way.
    //
    // /download rather than /stream, which is what it asked for before and only
    // ever worked because the two serve identical bytes. The difference is the
    // Content-Disposition filename, which is the whole reason the route exists.
    public async Task DownloadTrackAsync(string id, string destinationPath)
    {
        var partPath = destinationPath + PartialSuffix;

        // A previous attempt that died mid-transfer left its bytes here, so
        // ask for the rest instead of starting over - on a phone on flaky
        // wifi, a large FLAC otherwise restarts at byte 0 every time and may
        // never finish. Resuming only works because the caller's destination
        // path is deterministic per track (see LibraryDownloadService); a
        // random name per attempt would strand each partial instead.
        var alreadyHave = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        if (!await TryFetchAsync(id, partPath, alreadyHave))
        {
            // The server refused the range as unsatisfiable, which means our
            // partial is longer than the track now is - it is worthless.
            File.Delete(partPath);
            await TryFetchAsync(id, partPath, 0);
        }

        // Only now does the file take the name the library will record, so a
        // half-downloaded track is never mistaken for a playable one.
        File.Move(partPath, destinationPath, overwrite: true);
    }

    // Writes the track to partPath, appending to what is already there when
    // asked to resume from a non-zero offset. Returns false only when the
    // server rejected the range outright and the caller must retry from zero;
    // a failed *transfer* throws instead, deliberately leaving the partial in
    // place for the next attempt to resume from.
    private async Task<bool> TryFetchAsync(string id, string partPath, long from)
    {
        List<(string Key, string Value)> parameters = [("id", id)];
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildPlainUrl(DownloadPath, parameters));
        await AddPeerIdentityHeadersAsync(request, "GET", DownloadPath, parameters);
        if (from > 0)
            request.Headers.Range = new RangeHeaderValue(from, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (from > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return false;

        response.EnsureSuccessStatusCode(); // e.g. a 403 from a peer's trust gate - surfaces as a plain HttpRequestException.

        // A server is free to ignore Range and answer 200 with the whole body
        // (a peer on an older build does exactly that), and appending that to
        // a partial would corrupt it - so a 200 always overwrites, and only a
        // 206 appends. The full body is already on its way either way, so this
        // is handled by writing it rather than by asking again.
        var append = from > 0 && response.StatusCode == HttpStatusCode.PartialContent;

        await using var fileStream = new FileStream(partPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write);
        await response.Content.CopyToAsync(fileStream);
        return true;
    }

    // Kept next to the only two places that care (here and the resume check
    // above) rather than spelled inline, since the two must agree exactly.
    public const string PartialSuffix = ".part";
}
