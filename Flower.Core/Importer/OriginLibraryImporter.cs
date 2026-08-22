using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Services;

namespace Flower.Importer;

// RemoteLibraryImporter for a head whose "library" is simply the server it was
// served from, and which therefore has to find out who that server is first.
//
// RemoteLibraryImporter needs the origin's fingerprint up front: it stamps that
// onto every placeholder it returns, and a placeholder without one can never be
// streamed or downloaded, because nothing downstream knows who to ask (see
// Track.OriginDeviceFingerprint). A desktop client already holds it - a peer is
// discovered over mDNS and the fingerprint arrives with the discovery record,
// long before any catalog is pulled. The browser has no discovery at all, so it
// has to read the fingerprint off the /info handshake, which is deliberately
// ungated for exactly this reason (see DiscoveryEndpoints and PeerSignatureAuth:
// a peer must be able to learn who we are before either side can evaluate
// trust).
//
// That lookup lives here rather than at startup so that it happens on a path
// that is already asynchronous - the background rescan - instead of adding a
// round trip in front of the app's first frame. It is done once and remembered:
// a server does not change its keypair while a tab is open, and if it somehow
// did, every placeholder in hand would be wrong anyway and a reload is the
// honest answer.
public sealed class OriginLibraryImporter(
    HttpClient http,
    string baseUrl,
    IPeerCredentials credentials,
    ILogger<RemoteLibraryImporter> importerLogger,
    ILogger<OriginLibraryImporter> logger) : IMusicImporter
{
    private RemoteLibraryImporter? _importer;

    public bool ScansLocalFiles => false;

    public async Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null)
    {
        _importer ??= new RemoteLibraryImporter(
            http, baseUrl, credentials,
            originFingerprint: await ResolveOriginFingerprintAsync(),
            // Nothing has ever played anything here for the server to echo back
            // under our name - see RemoteLibraryImporter's own remarks on why
            // an empty string is the right answer rather than a missing one.
            ownFingerprint: string.Empty,
            importerLogger);

        return await _importer.ImportAsync(libraryPaths);
    }

    // Throws on a server that will not identify itself, rather than importing a
    // catalog of tracks that could never be played: the caller's rescan already
    // logs and survives a failed import, and an empty library is a far more
    // honest outcome than a full one made of dead rows.
    private async Task<string> ResolveOriginFingerprintAsync()
    {
        var info = await http.GetFromJsonAsync(
            $"{baseUrl.TrimEnd('/')}{SyncProtocol.InfoPath}",
            SyncProtocolJsonContext.Default.SyncInfoResponseDto);

        if (string.IsNullOrEmpty(info?.Fingerprint))
            throw new HttpRequestException($"{baseUrl} did not identify itself at {SyncProtocol.InfoPath}.");

        logger.LogInformation("Origin server at {BaseUrl} identified itself as {Alias} ({Fingerprint})",
            baseUrl, info.Alias, info.Fingerprint);
        return info.Fingerprint;
    }
}
