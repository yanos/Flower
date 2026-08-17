namespace Flower.Server.Configuration;

// Bound from the "Flower" section of appsettings.json/environment variables
// (see Program.cs). v1 auth is a single admin username/password - the
// pairing-code admin auth flow described in SYNC-PLAN.md's "Pairing
// redesign" section is a later build-order step, not this one.
public sealed class FlowerServerOptions
{
    public const string SectionName = "Flower";

    // Where flower.db and the log files live. Deliberately its own setting
    // rather than reusing Flower.Core's AppDataDirectory/PlatformDataDirectory -
    // those resolve a per-OS user profile directory, which makes no sense for
    // a headless server a user points at an arbitrary data volume (a NAS
    // share, a Docker volume mount, etc).
    public string DataDirectory { get; set; } = "./data";

    public List<string> LibraryPaths { get; set; } = [];

    // The password shipped in appsettings.json before this was a real
    // credential ("changeme"). Program.cs now refuses to start on it, or on
    // an empty one - the same value guards both the admin API and, through
    // SubsonicAuth, every /rest route, so a self-hoster who never edited the
    // config was one exposed port away from an open server.
    public const string PlaceholderAdminPassword = "changeme";

    public string AdminUsername { get; set; } = "admin";

    public string AdminPassword { get; set; } = "";

    // Whether LanGuard's built-in allow-list includes 100.64.0.0/10 - the
    // Tailscale range, and also generic carrier-grade NAT. See LanGuard's own
    // remarks; turn it off on a deployment that doesn't reach the server over
    // a tailnet.
    public bool TrustTailscaleRange { get; set; } = true;

    // Widens LanGuard's built-in private/loopback/CGNAT allow-list (see
    // Program.cs's LanGuard middleware) for a trusted tunnel/proxy whose
    // source range isn't already covered - e.g. a reverse proxy on its own
    // subnet. Empty by default: the built-in RFC1918 + Tailscale CGNAT
    // ranges already cover the documented "expose via Tailscale" path.
    public List<string> AllowedCidrs { get; set; } = [];
}
