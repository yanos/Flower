using System.Collections.Generic;

namespace Flower.Services;

// The browser head's IPeerCredentials: a server-minted session token presented
// as a bearer header, because a browser tab cannot sign anything.
//
// .NET-for-WebAssembly's crypto backend has no asymmetric crypto at all -
// ECDsa.Create() throws PlatformNotSupportedException for every curve - so
// there is no DeviceSigningKey there and SignedDeviceCredentials cannot be
// constructed (see App.axaml.cs's IsBrowser() branch). The token this carries
// is the one the desktop client's "Server Settings..." button puts in the page's
// URL fragment, or one the tab minted for itself; Flower.Server accepts it on
// the sync and stream-ticket routes as well as /api/admin (see
// Flower.Server/Services/PeerOrSessionAuth.cs, which also records why that
// widening is a deliberate trade rather than a detail).
//
// Sends nothing else - no fingerprint, no alias, no role. It does not need to:
// the server resolves all of that from the session itself, and identity params
// a receiver would take on trust from an unsigned caller are worse than absent.
//
// This is the entire browser-specific auth surface, which is what the
// IPeerCredentials seam was for. Replacing it with a real, non-extractable
// WebCrypto keypair later is a change to this one class plus whatever it takes
// to make signing asynchronous - not a sweep of every call site.
public sealed class AdminSessionCredentials(string token) : IPeerCredentials
{
    public const string HeaderName = "X-Flower-Admin-Session";

    // Read through a property rather than captured, so a renewed session can
    // replace the token in place and every holder picks it up - a jukebox tab
    // outlives the 60 minutes one session lasts (see AdminSessionService).
    public string Token { get; set; } = token;

    public IEnumerable<(string Key, string Value)> Authorize(
        string method, string absolutePath, IEnumerable<(string Key, string Value)> query, byte[] body) =>
        [(HeaderName, Token)];
}
