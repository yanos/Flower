using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.JavaScript;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Flower.Services;

// This tab's own device keypair, held behind WebCrypto.
//
// Same shape and the same job as DeviceSigningKey, which this deliberately does
// not try to be a subclass of: that one owns an ECDsa it can call synchronously,
// and crypto.subtle answers with a promise. What they share is the bytes -
// SignedRequestCanonicalizer builds the string, and the key formats line up
// exactly (see webcrypto.js), so the server verifies a browser's signature with
// the same SignatureVerifier as everything else.
//
// Nothing outside OperatingSystem.IsBrowser() may touch this: the JS module does
// not exist on any other platform. Same rule as BrowserLocation, and the same
// [JSImport] shape - Flower.Web's Program.Main imports the module before the app
// boots.
public sealed partial class BrowserSigningKey
{
    // Must match the name Flower.Web's Program.cs imports webcrypto.js under.
    public const string ModuleName = "webcrypto";

    private static partial class Interop
    {
        [JSImport("isAvailable", ModuleName)]
        public static partial bool IsAvailable();

        [JSImport("publicKey", ModuleName)]
        public static partial Task<string> PublicKeyAsync();

        [JSImport("sign", ModuleName)]
        public static partial Task<string> SignAsync(string payloadBase64);

        [JSImport("describe", ModuleName)]
        public static partial string Describe();
    }

    // False in an insecure context. crypto.subtle is exposed only over HTTPS and
    // on localhost, so a tab opened at http://192.168.x.y cannot hold a key and
    // therefore cannot be a device at all. That is a browser rule, not a choice
    // made here, and it is worth knowing about up front rather than discovering
    // at the first refused request - see BrowserPeerCredentials, which says so
    // once and then stops trying.
    public static bool IsAvailable => Interop.IsAvailable();

    public string PublicKeyBase64 { get; }
    public string Fingerprint { get; }
    public string Alias { get; }

    private BrowserSigningKey(string publicKeyBase64, string alias)
    {
        PublicKeyBase64 = publicKeyBase64;
        Alias = alias;
        Fingerprint = SignedRequestCanonicalizer.ComputeFingerprint(Convert.FromBase64String(publicKeyBase64));
    }

    // Loads this browser profile's key, generating one on first visit. The
    // private half never crosses this boundary in either direction - all that
    // comes back is the public point.
    public static async Task<BrowserSigningKey> LoadAsync() =>
        new(await Interop.PublicKeyAsync(), Interop.Describe());

    // Deliberately the same contract as DeviceSigningKey.Sign, including a fresh
    // timestamp and nonce per call: the receiving NonceReplayGuard treats a
    // repeated nonce as a replay, so nothing here may be computed once and kept.
    public async Task<(string Signature, string Timestamp, string Nonce)> SignAsync(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var toSign = SignedRequestCanonicalizer.Build(method, absolutePath, query, body, timestamp, nonce);
        var signature = await Interop.SignAsync(Convert.ToBase64String(toSign));
        return (signature, timestamp, nonce);
    }
}

// The browser head's IPeerCredentials: this tab signs, like every other Flower
// device.
//
// What this replaces was a server-minted bearer token carried in the page URL -
// 60 minutes, full admin, and (through what used to be PeerOrSessionAuth) the
// catalog and the right to mint stream tickets besides. On a LAN that was
// contained by LanGuard; a remote transport removes exactly that containment,
// which is why docs/OPEN-INTERNET-REVIEW.md finding 7 sequenced this before
// turning one on. There is no bearer credential in Flower any more.
//
// The bootstrap is the same one every other device uses: a single-use pairing
// code, which the desktop client's "Server Settings..." button or the server's
// own console puts in the URL fragment. It is spent within a second of the page
// loading and is worthless afterwards, where the token it replaced was live for
// its whole lifetime wherever the URL came to rest.
//
// Pairing is per browser profile, because the key is: each browser is its own
// row in the device list and each is revoked on its own. Clearing site data
// destroys the key and un-pairs that tab, and the way back is another code -
// the same recovery story as a lost phone.
public sealed class BrowserPeerCredentials(
    HttpClient http, Uri origin, string? pairingCode, ILogger<BrowserPeerCredentials> logger) : IPeerCredentials
{
    private const string RedeemPath = "/api/flower/v1/pair-redeem";

    // Started once and awaited by every caller. Lazy rather than done at
    // startup because it is a database open, a possible key generation and
    // possibly a network round-trip, and none of that belongs on the path that
    // puts a window on screen. Single-threaded runtime, so the null check needs
    // no lock - see the browser branch of App.RegisterServices.
    private Task<BrowserSigningKey?>? _identity;

    // Whether this tab holds a key the server has been told about. Null until
    // the first request forces the question.
    public Task<BrowserSigningKey?> IdentityAsync() => _identity ??= EstablishAsync();

    // Why this tab has no key, once we have tried to get one - null while it has
    // one, or before anything asked. Read by whoever has to explain a refusal to
    // a person: a 401 says "unauthenticated" and nothing more, and the guess a
    // caller makes from that alone ("not paired") is wrong in exactly the case
    // below, where there was never a key to pair. See
    // ServerAdminClient.explainUnauthorized.
    public string? UnauthenticatedReason { get; private set; }

    public async Task<IReadOnlyList<(string Key, string Value)>> AuthorizeAsync(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body)
    {
        var key = await IdentityAsync();
        if (key == null)
        {
            // Unsigned, which the server refuses. The caller sees a 401 and does
            // what it already does with one - an empty library, a track that
            // will not play - rather than an exception thrown up through a
            // dozen call sites that have no way to act on it. The reason was
            // logged once, at the point it was actually discovered.
            return [];
        }

        var identityParams = new List<(string Key, string Value)>
        {
            ("X-Flower-Fingerprint", key.Fingerprint),
            ("X-Flower-Alias", key.Alias),
            // A tab is a listener, never a host: it has no library of its own to
            // serve and nothing can sync *from* it. See SyncRolePolicy for what
            // the other heads make of this.
            ("X-Flower-Role", "client"),
            ("X-Flower-PublicKey", key.PublicKeyBase64),
        };

        var (signature, timestamp, nonce) = await key.SignAsync(
            method, absolutePath, query.Concat(identityParams), body);

        return
        [
            .. identityParams,
            ("X-Flower-Signature", signature),
            ("X-Flower-Timestamp", timestamp),
            ("X-Flower-Nonce", nonce),
        ];
    }

    private async Task<BrowserSigningKey?> EstablishAsync()
    {
        if (!BrowserSigningKey.IsAvailable)
        {
            UnauthenticatedReason =
                "This page cannot hold a device key, so it cannot be paired. Browsers only allow the " +
                "cryptography Flower signs with on a secure page: reach this server over https, or at " +
                "http://localhost if you are on the machine running it.";
            logger.LogError("{Reason}", UnauthenticatedReason);
            return null;
        }

        BrowserSigningKey key;
        try
        {
            key = await BrowserSigningKey.LoadAsync();
        }
        catch (Exception ex)
        {
            // A browser that refuses IndexedDB - private mode in some engines,
            // storage blocked by policy - lands here. Nothing to fall back to:
            // no key, no identity, no library.
            UnauthenticatedReason =
                "This browser would not let Flower store a device key, so this tab cannot be paired. " +
                "Private browsing, or a policy blocking site storage, is the usual cause.";
            logger.LogError(ex, "Could not open this browser's device key, so this tab cannot be paired");
            return null;
        }

        if (pairingCode != null)
            await RedeemAsync(key, pairingCode);

        return key;
    }

    // The same self-signed redeem every other device does (see
    // PeerPairingService.RedeemPairingCodeAsync and Flower.Server's
    // PairingEndpoints): the server has never seen this key, so the signature
    // proves only possession of it and the code supplies the authorization.
    //
    // A failure here is not fatal and does not clear the key. The likeliest
    // cause by far is a code that was already spent - a reload of a page whose
    // fragment survived, or a tab that was already paired - and in that case
    // this tab's signatures work perfectly well without it.
    private async Task RedeemAsync(BrowserSigningKey key, string code)
    {
        try
        {
            var (signature, timestamp, nonce) = await key.SignAsync("POST", RedeemPath, [], body: []);

            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, RedeemPath));
            request.Headers.TryAddWithoutValidation("X-Flower-Fingerprint", key.Fingerprint);
            request.Headers.TryAddWithoutValidation("X-Flower-Alias", key.Alias);
            request.Headers.TryAddWithoutValidation("X-Flower-PublicKey", key.PublicKeyBase64);
            request.Headers.TryAddWithoutValidation("X-Flower-PairingCode", code);
            request.Headers.TryAddWithoutValidation("X-Flower-Signature", signature);
            request.Headers.TryAddWithoutValidation("X-Flower-Timestamp", timestamp);
            request.Headers.TryAddWithoutValidation("X-Flower-Nonce", nonce);

            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
                logger.LogInformation("This browser is now paired with the server as {Alias} ({Fingerprint})", key.Alias, key.Fingerprint);
            else
                logger.LogInformation("The pairing code in the page URL was refused ({Status})", response.StatusCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not redeem the pairing code in the page URL");
        }
    }
}
