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
public sealed class FlowerServerOptions
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

    public List<string> LibraryPaths { get; set; } = [];

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

    // Widens LanGuard's built-in private/loopback/CGNAT allow-list (see
    // Program.cs's LanGuard middleware) for a trusted tunnel/proxy whose
    // source range isn't already covered - e.g. a reverse proxy on its own
    // subnet. Empty by default: the built-in RFC1918 + Tailscale CGNAT
    // ranges already cover the documented "expose via Tailscale" path.
    public List<string> AllowedCidrs { get; set; } = [];
}
