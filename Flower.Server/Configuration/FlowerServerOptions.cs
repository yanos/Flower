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

    public string AdminUsername { get; set; } = "admin";

    public string AdminPassword { get; set; } = "changeme";

    // Widens LanGuard's built-in private/loopback/CGNAT allow-list (see
    // Program.cs's LanGuard middleware) for a trusted tunnel/proxy whose
    // source range isn't already covered - e.g. a reverse proxy on its own
    // subnet. Empty by default: the built-in RFC1918 + Tailscale CGNAT
    // ranges already cover the documented "expose via Tailscale" path.
    public List<string> AllowedCidrs { get; set; } = [];
}
