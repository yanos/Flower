using Microsoft.Extensions.Options;

using Flower.Persistence;
using Flower.Models;
using Flower.Persistence.Sql;
using Flower.Server.Configuration;
using Flower.Server.Endpoints;
using Flower.Server.Services;
using Flower.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FlowerServerOptions>(builder.Configuration.GetSection(FlowerServerOptions.SectionName));

// TrustedPeerStore/DeviceKeyStore (Flower.Core) resolve their file paths via
// AppDataDirectory, which defaults to a per-OS user-profile directory -
// PlatformDataDirectory.Current overrides that, same hook the test suite
// uses (see Flower.Tests) to avoid writing into a real user's app-support
// folder. Must be set before anything touches those stores, and read
// straight off IConfiguration rather than through the DI container, which
// doesn't exist yet at this point in startup.
var dataDirectory = builder.Configuration.GetValue<string>($"{FlowerServerOptions.SectionName}:DataDirectory") ?? "./data";
PlatformDataDirectory.Current = Path.GetFullPath(dataDirectory);

// Nothing in this process should ever accept a body larger than the sync
// server's own ceiling (SyncHttpServer.MaxBodyBytes, 20 MB) - before this,
// only pair-redeem had a cap (4 KB, enforced by hand) and every other route
// inherited Kestrel's 30 MB default, with the LanGuard middleware the only
// thing between an unauthenticated caller and a 30 MB buffered upload.
// Per-endpoint caps still apply on top; this is the backstop.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 20 * 1024 * 1024);

// One FlowerDb for the process, exactly as the client registers it. It owns
// the connection string, the WAL/synchronous/foreign-key pragmas and the busy
// timeout, and it migrates itself on construction - so there is no separate
// "create the schema" step here to forget, and the server picks up a schema
// change the same way and at the same time the client does.
//
// The path is built from this app's own configured DataDirectory rather than
// FlowerDb.DefaultPath. Both resolve to <DataDirectory>/flower.db - the same
// file EF Core used - but DefaultPath goes through the process-global
// PlatformDataDirectory.Current, and the test suite boots several hosts with
// different data directories in one process, where whichever ran Program last
// would win for all of them.
builder.Services.AddSingleton(services =>
{
    var serverOptions = services.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
    return new FlowerDb(Path.Combine(serverOptions.DataDirectory, "flower.db"));
});
// Stateless over FlowerDb, so one instance for the process: it is the shared
// SQLite layer, registered here so the library and the importer write tracks
// through the same object the client does.
builder.Services.AddSingleton<TrackRepository>();

// The same resident Library the client runs on, for the life of the process,
// and the same type - there is no server-side wrapper around it. What the
// server adds is the TrackRepository below: handed in as Library's ITrackStore,
// it makes a star or a scrobble durable by the time the request is answered,
// and the PlaylistRepository does the same for a playlist edit. The client
// registers both the same way. Loaded from the database at startup below, then
// reconciled by each rescan.
builder.Services.AddSingleton<PlaylistRepository>();
builder.Services.AddSingleton(services => new Library(
    services.GetRequiredService<TrackRepository>().LoadAll(),
    services.GetRequiredService<ILogger<Library>>(),
    services.GetRequiredService<TrackRepository>(),
    services.GetRequiredService<PlaylistRepository>()));

builder.Services.AddScoped<LibraryImportService>();
builder.Services.AddSingleton<PairingCodeService>();
builder.Services.AddSingleton<StreamTicketService>();
builder.Services.AddSingleton<NonceReplayGuard>();
builder.Services.AddSingleton<TrustedPeerStore>();
builder.Services.AddSingleton<SubsonicCredentialStore>();
builder.Services.AddSingleton<LibraryManifestCache>();
builder.Services.AddSingleton<DeviceKeyStore>();

// Announces the server on the LAN so it shows up in a client's sidebar without
// anyone typing an address. Registered as a lifecycle service because the port
// it advertises has to be the one Kestrel actually bound - see MdnsAdvertiser.
builder.Services.AddSingleton<MdnsAdvertiser>();
builder.Services.AddHostedService(services => services.GetRequiredService<MdnsAdvertiser>());

// This server's own keypair, loaded once for the process exactly as the client
// does it in App.axaml.cs. It is not used to sign anything outbound here - what
// the server needs is its Fingerprint, which goes into every pairing invite so
// the redeeming device can pin the server's key instead of trusting whatever
// answers at that address. See PairingInvite.
builder.Services.AddSingleton(services =>
{
    var (key, publicKeyRaw) = services.GetRequiredService<DeviceKeyStore>().Load();
    return new DeviceSigningKey(key, publicKeyRaw);
});

var app = builder.Build();

// Break the bootstrap circularity: pairing codes are issued from /api/admin,
// and /api/admin can only be reached by a device that already paired as an
// admin, so a server nobody has ever administered can't be administered at
// all. Fix it where the circle is thinnest - if there is no admin peer on
// file, the server mints one admin-granting code itself and prints it, which
// on a headless box means it lands in `docker logs`.
//
// This replaces both the old startup check that refused to boot without a
// configured Flower:AdminPassword and the separate "first-run claim window"
// an earlier design had: there is no separate claim mechanism, just the first
// pairing code.
//
// `--pairing-code` forces the same print even when an admin is already on
// file, which is the way back in for an operator who cannot reach /api/admin
// any more - a lost browser profile, a device key regenerated underneath the
// app, an admin peer nothing holds the key to. Codes are in-memory
// (PairingCodeService), so this has to be a flag on the process that will
// answer the redeem, not a separate command against a running one. The only
// alternative was hand-editing trusted-peers.json to make HasAdmin() false
// again.
{
    var forcePairingCode = args.Contains("--pairing-code");
    var trustedPeers = app.Services.GetRequiredService<TrustedPeerStore>();
    if (forcePairingCode || !trustedPeers.HasAdmin())
    {
        var pairing = app.Services.GetRequiredService<PairingCodeService>();
        var signingKey = app.Services.GetRequiredService<DeviceSigningKey>();
        var serverOptions = app.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
        var (code, expiresAt) = pairing.GenerateCode(grantsAdmin: true);

        // Deliberately not the ILogger: this is meant for a human reading a
        // terminal or `docker logs` right now, and it must not be swallowed by
        // a log level, routed to a file, or shipped off to a log aggregator
        // where a live credential has no business being.
        var host = string.IsNullOrWhiteSpace(serverOptions.AdvertisedHost)
            ? "<this-server>:4533"
            : serverOptions.AdvertisedHost;
        Console.WriteLine();
        Console.WriteLine(forcePairingCode
            ? "  Issuing an admin pairing code (--pairing-code)."
            : "  No device can administer this server yet.");
        Console.WriteLine($"  Pair one with this code (valid until {expiresAt.ToLocalTime():HH:mm:ss}): {code}");
        Console.WriteLine($"  Or open: {new PairingInvite(host, code, signingKey.Fingerprint)}");
        Console.WriteLine();
    }
}

app.Use(async (context, next) =>
{
    var serverOptions = context.RequestServices.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress == null
        || !LanGuard.IsPrivateOrLoopback(remoteAddress, serverOptions.AllowedCidrs, serverOptions.TrustTailscaleRange))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});

using (var scope = app.Services.CreateScope())
{
    // No migration call here: FlowerDb applies SqliteMigrations in its own
    // constructor, so the schema is current before the first query can be
    // issued. This used to be EnsureCreatedAsync(), which stamps no schema
    // version at all and left a self-hoster with a silently stale table and no
    // upgrade path but deleting flower.db (ARCHITECTURE-REVIEW Tier 2.5); a
    // schema change is now an appended script in Flower.Core's Schema.
    var importService = scope.ServiceProvider.GetRequiredService<LibraryImportService>();
    importService.LoadStored();
    await importService.RescanAsync();
}

app.MapSubsonicEndpoints();
app.MapAdminEndpoints();
app.MapPairingEndpoints();
app.MapSyncEndpoints();
app.MapStreamTicketEndpoints();
app.MapDiscoveryEndpoints();

app.Run();

// Exposed so Flower.Server.Tests's WebApplicationFactory<Program> can boot the
// real app in-process. Top-level statements otherwise compile to an internal
// Program class the test project cannot name.
public partial class Program;
