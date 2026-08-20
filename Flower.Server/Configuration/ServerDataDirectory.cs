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
            "_AdvertiseOnLan": "false to stop announcing this server over mDNS - for tailnet/reverse-proxy-only deployments."
          }
        }

        """);
    }
}
