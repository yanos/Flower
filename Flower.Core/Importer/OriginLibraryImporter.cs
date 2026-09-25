using System;
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

    // Whether the origin server holds a key for this client, as it said on the
    // /info handshake: false is "never paired, or revoked since", null is that
    // it said nothing about it - the request carried no identity (a page that
    // cannot hold a key), or its signature did not verify on the way.
    //
    // It is the one thing an unpaired browser tab needs to be told. Every
    // request it makes is refused, and before this it showed an empty library
    // and a settings page whose every button came back "not paired", with no
    // word of how pairing happens. See MainViewModel.BrowserPairingProblem.
    public bool? OriginTrustsThisClient { get; private set; }

    // Whether the server made this client one of its administrators - the
    // same /info answer, read off the same signed handshake. What decides
    // whether the browser head offers its Server Settings page at all: a
    // listener's tab would only ever be refused there. Null when the server
    // did not say, which is read as no.
    public bool? OriginCallerIsAdmin { get; private set; }

    // Raised once the /info handshake has answered, before the catalog is
    // fetched: the two flags above are what a browser tab needs to decide what
    // to show, and waiting for a whole library to arrive first would leave it
    // showing the wrong thing in the meantime.
    public event Action? HandshakeAnswered;

    public async Task<List<Track>> ImportAsync(IEnumerable<string>? libraryPaths = null)
    {
        if (_importer == null)
        {
            var originFingerprint = await ResolveOriginFingerprintAsync();

            // Not asked for a catalog it would refuse, and not remembered
            // either: the next import asks again, which is how a tab that pairs
            // in the meantime gets its library without a reload.
            if (OriginTrustsThisClient == false)
            {
                logger.LogWarning("Origin server at {BaseUrl} does not know this client - it has not been paired "
                    + "with it, or was unpaired. Nothing to import until it is.", baseUrl);
                return [];
            }

            _importer = CreateImporter(originFingerprint);
        }

        return await _importer.ImportAsync(libraryPaths);
    }

    private RemoteLibraryImporter CreateImporter(string originFingerprint) =>
        new(http, baseUrl, credentials,
            originFingerprint: originFingerprint,
            // Nothing has ever played anything here for the server to echo back
            // under our name - see RemoteLibraryImporter's own remarks on why
            // an empty string is the right answer rather than a missing one.
            ownFingerprint: string.Empty,
            importerLogger);

    // Throws on a server that will not identify itself, rather than importing a
    // catalog of tracks that could never be played: the caller's rescan already
    // logs and survives a failed import, and an empty library is a far more
    // honest outcome than a full one made of dead rows.
    //
    // Signed, although /info is ungated, because that is what makes the server
    // say whether it knows us (TrustsCaller) - unsigned, it has nobody to
    // answer about. Signing is also what spends a pairing code the page
    // arrived with (BrowserPeerCredentials redeems on first use), so a tab
    // that has just paired is already trusted by the time this asks.
    private async Task<string> ResolveOriginFingerprintAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}{SyncProtocol.InfoPath}");
        await request.AddPeerCredentialsAsync(credentials);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var info = await response.Content.ReadFromJsonAsync(SyncProtocolJsonContext.Default.SyncInfoResponseDto);
        OriginTrustsThisClient = info?.TrustsCaller;
        OriginCallerIsAdmin = info?.CallerIsAdmin;
        HandshakeAnswered?.Invoke();

        if (string.IsNullOrEmpty(info?.Fingerprint))
            throw new HttpRequestException($"{baseUrl} did not identify itself at {SyncProtocol.InfoPath}.");

        logger.LogInformation("Origin server at {BaseUrl} identified itself as {Alias} ({Fingerprint})",
            baseUrl, info.Alias, info.Fingerprint);
        return info.Fingerprint;
    }
}
