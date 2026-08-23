using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

// For ILoggingBuilder.AddSerilog - the file-writing engine AppLogging just
// configured. Application code still logs through Microsoft.Extensions.
// Logging's ILogger everywhere, never Serilog's own.
using Serilog;

using Flower.Logging;
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
// AppDataDirectory; PlatformDataDirectory.Current overrides that, the same
// hook the test suite uses (see Flower.Tests) to avoid writing into a real
// user's app-support folder. Must be set before anything touches those
// stores, and read straight off IConfiguration rather than through the DI
// container, which doesn't exist yet at this point in startup.
var dataDirectory = ServerDataDirectory.Resolve(
    builder.Configuration.GetValue<string>($"{FlowerServerOptions.SectionName}:DataDirectory"));
Directory.CreateDirectory(dataDirectory);
PlatformDataDirectory.Current = dataDirectory;

// Config comes from two places on purpose. appsettings.json ships next to the
// binary and carries the defaults; flower-server.json lives in the data
// directory, which is what an operator actually owns and keeps across an
// upgrade, a container rebuild or a reinstall - so that is where a setting the
// operator changed belongs.
//
// It has to sit above the appsettings files and below everything else, so it
// is moved into position rather than appended: a source added at the end
// outranks the environment and the command line, and a container setting
// Flower__Alias or ASPNETCORE_URLS would then be silently overruled by a file
// on its data volume. AddJsonFile builds the source (file provider, reload
// token, the lot) correctly - all this does is put it back in the chain one
// slot after the last appsettings file.
ServerDataDirectory.SeedSettingsFile(dataDirectory);
builder.Configuration.AddJsonFile(
    Path.Combine(dataDirectory, ServerDataDirectory.SettingsFileName), optional: true, reloadOnChange: true);
{
    var sources = builder.Configuration.Sources;
    var settingsSource = sources[^1];
    var lastAppSettings = sources.Count - 1;
    while (lastAppSettings > 0 && sources[lastAppSettings - 1] is not JsonConfigurationSource)
        lastAppSettings--;
    sources.RemoveAt(sources.Count - 1);
    sources.Insert(lastAppSettings, settingsSource);
}

// The one setting that cannot come from flower-server.json - it is what found
// that file - written back as the resolved absolute path so everything reading
// IOptions<FlowerServerOptions> (FlowerDb's path, below) agrees with what
// PlatformDataDirectory.Current was just set to, rather than re-resolving a
// relative path against whatever the working directory happens to be.
builder.Configuration.AddInMemoryCollection(
    [new KeyValuePair<string, string?>($"{FlowerServerOptions.SectionName}:DataDirectory", dataDirectory)]);

// File logging, into <DataDirectory>/logs - the same Serilog bootstrap the app
// uses (AppLogging.LogsDirectory resolves through the PlatformDataDirectory
// just set), rather than a second configuration of the same sinks. Until now
// this server logged to the console only, which on a headless box means a
// crash at 3am is whatever the init system happened to retain.
//
// ClearProviders first: AppLogging's own console sink replaces the default
// console provider rather than doubling every line. The Logging:LogLevel
// section still applies on top of Serilog's minimum level, so appsettings.json
// remains the way to turn the noise up or down.
var logFile = AppLogging.Initialize(fileSizeLimitBytes: 32 * 1024 * 1024);
builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

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
// Short-lived derived authority for the browser settings page, which cannot sign
// its own requests - see AdminSessionService for why that is a property of the
// WebAssembly runtime rather than a shortcut taken here.
builder.Services.AddSingleton<AdminSessionService>();
// Owns "a rescan is running", so the admin API can start one without two
// operators racing two importers over the same folders.
builder.Services.AddSingleton<LibraryRescanCoordinator>();
builder.Services.AddSingleton<NonceReplayGuard>();
builder.Services.AddSingleton<TrustedPeerStore>();
builder.Services.AddSingleton<SubsonicCredentialStore>();
builder.Services.AddSingleton<LibraryManifestCache>();
builder.Services.AddSingleton<PlayReportService>();
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

app.Logger.LogInformation("Data directory: {DataDirectory}", dataDirectory);
app.Logger.LogInformation("Logging to: {LogFile}", logFile);

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

        // The web UI is administered by a token minted by an admin device, and
        // at this point there is no admin device - so the console mints one for
        // itself. Printed under exactly the same gate as the pairing code above,
        // never on an ordinary boot: this is a live credential, which is also why
        // it goes to stdout rather than through the ILogger.
        //
        // Addressed differently from the pairing invite above: that one is for
        // some *other* device, so a wildcard bind honestly reads as
        // "<this-server>", while this one is for whoever is reading this console,
        // who is on the machine. So it resolves the configured bind address and
        // turns a wildcard into localhost, giving a link that can just be clicked.
        var browserHost = ResolveLocalHost(builder.Configuration["Urls"]) ?? host;
        Console.WriteLine();
        Console.WriteLine("  Settings in a browser (valid for one hour):");
        Console.WriteLine($"  {WebUiHosting.BuildConsoleSessionUrl(app.Services.GetRequiredService<AdminSessionService>(), browserHost)}");
        Console.WriteLine();
    }
}

// Ahead of the LanGuard gate below, and that order is the whole point: this is
// what decides which address that gate - and every per-IP rate limiter behind
// it - is actually looking at.
//
// Only runs when an operator has named a proxy (FlowerServerOptions
// .TrustedProxies). Unconfigured, no X-Forwarded-For is believed from anyone,
// which is the safe default for the ordinary "clients reach Kestrel directly"
// deployment - there, a forwarded header can only have been written by the
// client itself.
//
// KnownIPNetworks/KnownProxies are cleared first because they are not empty by
// default (loopback is trusted out of the box), and "trusted unless the
// operator says otherwise" is the wrong shape for this particular decision:
// on a box where anything else is listening, loopback is reachable by every
// local process.
var trustedProxies = app.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value.TrustedProxies;
if (trustedProxies.Count > 0)
{
    var forwarded = new ForwardedHeadersOptions
    {
        // For and Proto, not Host. The first two are what a TLS-terminating
        // proxy genuinely knows better than Kestrel does; the host a pairing
        // invite should name already has an explicit override that an operator
        // sets deliberately (FlowerServerOptions.AdvertisedHost, see
        // AdminEndpoints.BuildInvite), and quietly rewriting Host underneath it
        // would give the same setting two sources of truth.
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,

        // A ceiling on how far back through the chain to walk, not a grant of
        // trust to that many hops: the middleware re-checks each address it
        // pops against the networks below and stops at the first one it does
        // not recognise. Sized from the configured list because the deployment
        // this exists for has one entry and one hop, and a chain can only get
        // longer by an operator naming the extra hops here too.
        ForwardLimit = trustedProxies.Count,
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();

    foreach (var cidr in trustedProxies)
    {
        // System.Net's parser is strict about the address being the network's
        // base one, so 192.168.1.5/24 is rejected rather than quietly read as
        // 192.168.1.0/24 - hence the warning naming both ways this can fail.
        if (System.Net.IPNetwork.TryParse(cidr, out var parsed))
            forwarded.KnownIPNetworks.Add(parsed);
        else
            app.Logger.LogWarning(
                "Ignoring {Cidr} in TrustedProxies: not a CIDR, or not written as the network's base address (192.168.1.0/24, not 192.168.1.5/24). Nothing forwarded by it will be believed",
                cidr);
    }

    app.UseForwardedHeaders(forwarded);
    app.Logger.LogInformation(
        "Believing X-Forwarded-For from {ProxyCount} configured proxy network(s): {Proxies}",
        forwarded.KnownIPNetworks.Count, string.Join(", ", trustedProxies));
}

app.Use(async (context, next) =>
{
    // Monitor, not IOptions: the allow-list is editable from the admin API, and
    // IOptions binds once for the life of the process - a CIDR added in the
    // browser would then not apply until a restart, which is exactly the setting
    // an operator is most likely to be changing *because* they are locked out.
    var serverOptions = context.RequestServices.GetRequiredService<IOptionsMonitor<FlowerServerOptions>>().CurrentValue;
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

// Last, so its single-page fallback can only ever catch what no API route did.
app.MapWebUi();

app.Run();

// The last few lines of a run are buffered otherwise - same reason
// MainWindow's Closing handler calls this in the app.
AppLogging.Shutdown();

// "localhost:4533" out of "http://0.0.0.0:4533" - the address to type into a
// browser running on this machine. Null when there is nothing configured to
// resolve, in which case the caller keeps its own fallback.
static string? ResolveLocalHost(string? configuredUrls)
{
    var first = configuredUrls?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    if (first == null || !Uri.TryCreate(first.Trim(), UriKind.Absolute, out var uri))
        return null;

    // A wildcard bind is not an address anything can dial; the loopback name is
    // the one that always reaches it from here.
    var hostName = uri.Host is "0.0.0.0" or "[::]" or "::" or "+" or "*" ? "localhost" : uri.Host;
    return $"{hostName}:{uri.Port}";
}

// Exposed so Flower.Server.Tests's WebApplicationFactory<Program> can boot the
// real app in-process. Top-level statements otherwise compile to an internal
// Program class the test project cannot name.
public partial class Program;
