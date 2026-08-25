using Flower.Persistence;

namespace Flower.Server.Configuration;

// Where this server keeps everything it owns at runtime: flower.db, the device
// key, the trusted/denied peer files, the logs, and the operator-editable
// settings file. Resolved once, before anything touches a store, and pushed
// back into configuration so that IOptions<FlowerServerOptions>.DataDirectory
// is always the same absolute path (see Program.cs).
//
// The default used to be "./data" - relative to the *current working
// directory*, which is a real trap: `dotnet run` from the repo put it in the
// project folder, a published binary put it wherever the operator's shell
// happened to be, and a systemd unit without a WorkingDirectory put it in /.
// Same install, three different libraries, none of them where anyone would
// look. It now defaults to the per-OS user data location, the same place the
// app itself keeps its data.
//
// Deliberately a subdirectory of it rather than the directory itself: on a
// developer machine the desktop app and this server run side by side, and
// sharing AppDataDirectory.Path would mean sharing device-key.json - both
// processes would then present the *same* device fingerprint, which is the one
// thing pairing cannot survive (a client would be pairing with itself). They
// share flower.db in that arrangement too, with two writers on one library.
public static class ServerDataDirectory
{
    // Operator-editable settings, layered over the appsettings.json shipped
    // next to the binary. See Program.cs for why config lives in two places.
    public const string SettingsFileName = "flower-server.json";

    public static string Resolve(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppDataDirectory.Path, "Server")
            : Path.GetFullPath(configured);

    // Written once, when the directory is first created, purely so an operator
    // who goes looking finds a file with the knobs in it rather than an empty
    // folder. Everything in it is underscore-prefixed except the section
    // itself: a real key here would silently outrank the same key in
    // appsettings.json, so a seeded "LibraryPaths": [] would blank out a path
    // the operator had configured there.
    public static void SeedSettingsFile(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, SettingsFileName);
        if (File.Exists(path))
            return;

        File.WriteAllText(path, """
        {
          "_": "Settings for this Flower server. Anything under 'Flower' here overrides the appsettings.json shipped next to the binary, and survives redeploying it; environment variables (Flower__Alias=...) and command-line switches still win over this file. DataDirectory is the one setting that cannot go here - it is what locates this file - so set that one via appsettings.json or Flower__DataDirectory.",
          "Flower": {
            "_LibraryPaths": ["Folders to scan for music, e.g. /music or /srv/media/library."],
            "_Alias": "The name this server shows up as in a Flower client's sidebar. Defaults to the machine name.",
            "_AdvertisedHost": "host:port to put in pairing invites when the address a device should dial isn't the one the request arrived on (reverse proxy, remapped container port).",
            "_AdvertiseOnLan": "false to stop announcing this server over mDNS - for tailnet/reverse-proxy-only deployments.",
            "_IntegrateWithITunes": "false to make this server ignore a local iTunes/Music.app library entirely. On by default: Music.app's own media folder is added to LibraryPaths on the first scan that finds it, and the two switches below then import play counts and Date Added from it.",
            "_SyncPlayCountFromITunes": "false to stop importing per-track play counts from Music.app on each rescan.",
            "_SyncDateAddedFromITunes": "false to stop importing per-track Date Added values from Music.app on each rescan.",
            "_HttpsPort": "The port for this server's TLS listener, alongside the plain one - 4534 by default, 0 to turn it off. Needs no configuration: the certificate is minted from this server's own device key, which every paired Flower client already holds, so it validates without a certificate authority. A browser and a third-party OpenSubsonic client cannot do that and keep using the plain port; set CertificatePath for them.",
            "_CertificatePath": "A real certificate to serve instead of the self-signed one, as a PEM file - from 'tailscale cert', Let's Encrypt, or a domain you own. Must be set together with CertificateKeyPath.",
            "_CertificateKeyPath": "The private key for CertificatePath, as a PEM file.",
            "_AllowPublicAccess": "true only if this server is deliberately reachable from the internet, through a tunnel or a mapped port - it turns the LAN allow-list off entirely, leaving each paired device's signature as the only thing gating access. Set TrustedProxies alongside it whenever a proxy is what makes the server reachable. Applies immediately and is warned about at every start; it is also the settings page's 'Accept connections from outside this network'.",
            "_AllowedCidrs": ["Extra networks allowed to reach this server, e.g. 10.8.0.0/24. Private, loopback and Tailscale addresses are already allowed."],
            "_TrustedProxies": ["Networks whose X-Forwarded-For this server believes, e.g. 127.0.0.1/32. Empty by default, so no forwarded header is trusted from anyone. Set it to the proxy in front of this server (127.0.0.1/32 for 'tailscale serve'), or every client behind that proxy is seen as one address and shares one rate-limit bucket. Read once at startup."],
            "_WebUiPath": "Override for where the browser UI lives. Empty looks next to the binary (where the build puts it), then here. Set, it is the only place looked at."
          }
        }

        """);
    }
}
