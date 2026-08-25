using Flower.Persistence;

namespace Flower.Server.Configuration;

// Bound from the "Flower" section of appsettings.json/environment variables
// (see Program.cs).
//
// Note what is *not* here: there is no admin username or password, and no
// credential of any kind. Under SYNC-PLAN.md's "Passwordless by design" every
// Flower surface - including the browser admin UI - authenticates with a
// device keypair it obtained by redeeming a pairing code, and third-party
// Subsonic clients use per-client credentials this server generates at
// runtime (SubsonicCredentialStore). Nothing an operator has to invent, type
// into a config file, or remember to change from a placeholder.
//
// Derives from MusicLibrarySettings, which the app's own AppSettings derives
// from too: the library folders and the three iTunes switches are the same
// settings on both hosts, and were briefly declared (and defaulted, and
// documented) twice. Everything left here is deployment configuration the app
// has no equivalent of.
public sealed class FlowerServerOptions : MusicLibrarySettings
{
    public const string SectionName = "Flower";

    // Where flower.db, the device key, the peer files, the logs and
    // flower-server.json live. Empty means the per-OS user data location
    // (see ServerDataDirectory.Resolve) - the point of the setting is the
    // deployment that wants an arbitrary data volume instead: a NAS share, a
    // Docker volume mount, a dedicated disk.
    //
    // Read by Program.cs before the container exists, and written back there
    // as an absolute path - so by the time this is bound it is never relative
    // and never empty, whatever was configured.
    public string DataDirectory { get; set; } = "";

    // Host:port to put in a pairing invite's QR code, for deployments where
    // the address the request arrived on is not the address a new device
    // should dial - a reverse proxy, or a container publishing a different
    // port than it binds. Empty means "use the request's own Host header",
    // which is right for the direct LAN and tailnet cases and so is the
    // default. See AdminEndpoints.BuildInvite.
    public string AdvertisedHost { get; set; } = "";

    // The name this server announces over mDNS and reports from
    // SyncProtocol.InfoPath - what a user sees in a Flower client's sidebar.
    // Defaults to the machine name, which is right for the ordinary
    // one-server-on-the-LAN case and wrong the moment there are two.
    public string Alias { get; set; } = "";

    // Whether to announce this server on the local network at all. On by
    // default: a self-hoster on their own LAN wants the client to find the
    // server without being told an address. Turn it off for a deployment
    // reached only over a tailnet or a reverse proxy, where the multicast
    // announcement is noise that can never be heard by the clients that
    // matter.
    public bool AdvertiseOnLan { get; set; } = true;

    // Whether LanGuard's built-in allow-list includes 100.64.0.0/10 - the
    // Tailscale range, and also generic carrier-grade NAT. See LanGuard's own
    // remarks; turn it off on a deployment that doesn't reach the server over
    // a tailnet.
    public bool TrustTailscaleRange { get; set; } = true;

    // An override for where the browser UI lives. Normally empty, which means
    // "next to the binary, then in the data directory" - and next to the binary
    // is where Flower.Server's own build puts it, so the usual case needs no
    // configuration at all. Setting this makes it the *only* place looked at,
    // rather than one candidate among several: a path an operator named
    // deliberately should not silently fall through to some other bundle.
    // See WebUiHosting.Resolve.
    public string WebUiPath { get; set; } = "";

    // The port for this server's https listener, alongside - not instead of -
    // the plain one in Urls. 0 turns TLS off entirely.
    //
    // A second port rather than a scheme change on 4533, because the plain
    // listener has callers that cannot follow: third-party OpenSubsonic
    // clients configured with an http address, and anything holding a
    // bookmark. Serving both costs a port and breaks nothing, and Flower's own
    // clients move over on their own - the server lists its https origins from
    // /info and the client's candidate ranking prefers them (see
    // LocalAddresses.Reachable's scheme parameter, and REMOTE-ACCESS-PLAN.md).
    //
    // On by default, and that is the point: with a self-signed certificate
    // minted from this server's own device key, TLS needs no certificate
    // authority, no domain and no operator action at all - a paired client
    // already holds the key that validates it. See ServerTls and
    // DeviceCertificate.
    public int HttpsPort { get; set; } = 4534;

    // A real certificate to serve instead of the self-signed one, as a PEM
    // pair. Empty means self-signed, which is right for every client that
    // paired with this server; set these for the two that cannot pin - a
    // browser tab and a third-party OpenSubsonic client. Both files are
    // needed; naming only one is a configuration error and startup says so.
    public string CertificatePath { get; set; } = "";
    public string CertificateKeyPath { get; set; } = "";

    // Whether this server is deliberately reachable from the open internet,
    // which switches LanGuard's gate off entirely (see Program.cs).
    //
    // Off by default, and the default is the whole point: LanGuard is cited as
    // the containing control in five separate places that were each reasoned
    // about on their own (docs/OPEN-INTERNET-REVIEW.md), so retiring it is one
    // decision with five consequences and deserves to be one setting rather
    // than a side effect. The same effect is expressible as an AllowedCidrs of
    // 0.0.0.0/0 - this exists so that nobody has to write that and hope a
    // reader notices what it means.
    //
    // What carries the weight afterwards, all of which is already there: every
    // route that matters requires a device signature (PeerSignatureAuth), the
    // per-client rate limits key on the forwarded address rather than the
    // proxy's, and the browser head holds a non-extractable key rather than a
    // bearer token. LanGuard was the belt; those are the braces.
    //
    // Set TrustedProxies alongside it whenever a tunnel or proxy is what makes
    // the server reachable, or every remote listener arrives as that proxy's
    // address and shares one rate-limit bucket. Startup says so if it is not.
    //
    // Editable from the settings page, unlike TrustedProxies below. That was
    // once the other way round - a server's exposure is a fact about how it was
    // deployed, and the page served from it is a strange place to decide it -
    // but the page already offers AllowedCidrs, and an operator who wants this
    // could always get it by typing 0.0.0.0/0 into that box. Which is the worse
    // outcome of the two: the same door, opened by a control that does not look
    // like it is opening it. So it is a switch that says what it does, with the
    // server's own addresses beside it, and it is the admin surface - a paired
    // device holding an admin key - that can throw it.
    //
    // Read per request through IOptionsMonitor (see Program.cs's gate), so
    // toggling it takes effect immediately and needs no restart. That matters
    // most in the direction that closes the door.
    public bool AllowPublicAccess { get; set; } = false;

    // Widens LanGuard's built-in private/loopback/CGNAT allow-list (see
    // Program.cs's LanGuard middleware) for a trusted tunnel/proxy whose
    // source range isn't already covered - e.g. a reverse proxy on its own
    // subnet. Empty by default: the built-in RFC1918 + Tailscale CGNAT
    // ranges already cover the documented "expose via Tailscale" path.
    public List<string> AllowedCidrs { get; set; } = [];

    // The proxies whose X-Forwarded-For this server believes, as CIDRs.
    //
    // Empty by default, and that default is the security-relevant half: an
    // X-Forwarded-For header is written by whoever sent the request, so
    // honouring one from an arbitrary caller would hand every caller a free
    // way to pick their own source address - past LanGuard's allow-list and
    // out of whatever per-IP bucket they had exhausted. Only a hop named here
    // is believed.
    //
    // The documented reason to set it is `tailscale serve` (docs/SELF-HOSTING
    // .md): tailscaled terminates TLS and proxies onward over loopback, so
    // without this every tailnet device arrives as 127.0.0.1 and shares one
    // rate-limit bucket with all the others. Set it to 127.0.0.1/32 there.
    //
    // Deliberately not in ServerSettingsDto, unlike AllowedCidrs: this is a
    // fact about how the server was deployed rather than a preference, and
    // the browser page served *through* the proxy is the last place that
    // should be able to change who the proxy is trusted to be. Same reasoning
    // as WebUiPath above, and the same consequence - it is read once at
    // startup, so changing it needs a restart.
    public List<string> TrustedProxies { get; set; } = [];
}
