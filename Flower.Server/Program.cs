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
// console provider rather than doubling every line.
//
// Logging:LogLevel is read here and handed to Serilog rather than left to
// Microsoft.Extensions.Logging, which is what it looks like it configures. It
// isn't: AddSerilog registers a provider-scoped Trace rule that outranks every
// rule the section can produce, so the section was inert and the levels an
// operator set in appsettings.json - or on the command line - changed nothing.
// LogLevelSettings translates it into the floor and the per-category overrides
// Serilog does honour, so the familiar keys keep their familiar meaning.
var (logFloor, logOverrides) = LogLevelSettings.Read(builder.Configuration);
var logFile = AppLogging.Initialize(
    fileSizeLimitBytes: 32 * 1024 * 1024, minimumLevel: logFloor, categoryOverrides: logOverrides);
builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

// Nothing in this process should ever accept a body larger than 20 MB (the
// ceiling the app's own listener used, back when there were two) - before this,
// only pair-redeem had a cap (4 KB, enforced by hand) and every other route
// inherited Kestrel's 30 MB default, with the LanGuard middleware the only
// thing between an unauthenticated caller and a 30 MB buffered upload.
// Per-endpoint caps still apply on top; this is the backstop.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = 20 * 1024 * 1024);

// This server's own keypair, loaded here rather than left to the container
// because the https listener below needs it before the container exists. The
// same instance is registered as DeviceSigningKey further down, so the process
// still holds exactly one - loading twice would read the same file and work,
// but it would also quietly create a second ECDsa over identical material and
// invite the two to drift if the store ever gained caching.
//
// The logger comes from AppLogging's own factory rather than a LoggerFactory
// built inline here: that one was never disposed, and it was a second factory
// wrapping the very same Log.Logger this one does. Registering it also makes
// AppLogging.CreateLogger<T>() work for the rest of this process - without
// this call it returns NullLogger, so any Flower.Core class with a static
// logger field was silently mute server-side while logging fine in the app.
AppLogging.UseLoggerFactory(LoggerFactory.Create(logging => logging.AddSerilog()));
var (deviceKey, devicePublicKeyRaw) = new DeviceKeyStore(
    AppLogging.CreateTypedLogger<DeviceKeyStore>()).Load();

// TLS, alongside the plain listener rather than instead of it.
//
// The reason this needs no configuration is DeviceCertificate: the certificate
// is minted from the keypair just loaded, and a client that paired with this
// server already stores that key, so it can validate the certificate against
// something it has rather than against an authority nobody set up. The plain
// port stays exactly as it was for the one caller that cannot do that: a
// browser tab, which has no pin to check a certificate against, plus anything
// holding an http bookmark.
//
// UseUrls rather than a Kestrel Listen call, deliberately: an explicit Listen
// makes Kestrel ignore the Urls configuration entirely, which would silently
// discard an operator's ASPNETCORE_URLS or a container's published bind
// address. Adding a URL to the list keeps every existing way of configuring
// the http listener working untouched.
{
    var httpsPort = builder.Configuration.GetValue($"{FlowerServerOptions.SectionName}:HttpsPort", 4534);
    var certificatePath = builder.Configuration[$"{FlowerServerOptions.SectionName}:CertificatePath"] ?? "";
    var certificateKeyPath = builder.Configuration[$"{FlowerServerOptions.SectionName}:CertificateKeyPath"] ?? "";

    if (httpsPort > 0)
    {
        // Named one but not the other: refusing to start is right here rather
        // than falling back to self-signed, because an operator who configured
        // a certificate did so for the callers that cannot pin, and silently
        // serving one those callers reject would present as "TLS is broken"
        // with nothing in the logs pointing at the typo.
        if (string.IsNullOrWhiteSpace(certificatePath) != string.IsNullOrWhiteSpace(certificateKeyPath))
        {
            throw new InvalidOperationException(
                $"{FlowerServerOptions.SectionName}:CertificatePath and " +
                $"{FlowerServerOptions.SectionName}:CertificateKeyPath must be set together.");
        }

        var certificate = string.IsNullOrWhiteSpace(certificatePath)
            ? ServerTls.SelfSigned(deviceKey, Environment.MachineName)
            : ServerTls.FromFiles(certificatePath, certificateKeyPath);

        // The host of the existing http listener, so the https one binds the
        // same way: a wildcard bind stays a wildcard, and a deployment that
        // deliberately bound one interface does not get a second listener
        // quietly opened on all of them.
        var httpsHost = ResolveBindHost(builder.Configuration["Urls"]) ?? "0.0.0.0";
        var urls = (builder.Configuration["Urls"] ?? "http://0.0.0.0:4533")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        builder.WebHost.UseUrls([.. urls, $"https://{httpsHost}:{httpsPort}"]);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = certificate));
    }
}

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
    return new FlowerDb(
        Path.Combine(serverOptions.DataDirectory, "flower.db"),
        services.GetRequiredService<ILogger<FlowerDb>>());
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
// Keeps smart playlists in step with the catalog. Registered on the server as
// well as in the app, and not only for symmetry: a listener's play reported in
// over /api/flower/v1/plays is a smart-playlist input, and the server's own
// materialized playlist_tracks rows are what it serves from.
builder.Services.AddSingleton<SmartPlaylistRefresher>();
builder.Services.AddSingleton<PairingCodeService>();
builder.Services.AddSingleton<StreamTicketService>();
// Owns "a rescan is running", so the admin API can start one without two
// operators racing two importers over the same folders.
builder.Services.AddSingleton<LibraryRescanCoordinator>();
builder.Services.AddSingleton<NonceReplayGuard>();
builder.Services.AddSingleton<TrustedPeerStore>();
builder.Services.AddSingleton<LibraryManifestCache>();
builder.Services.AddSingleton<PlayReportService>();
// Where a paired device's pushed log snapshot lands (SyncEndpoints'
// /log/report) and is read back from (AdminEndpoints' /devices/{fp}/logs).
// Kept as timestamped JSON Lines under <DataDirectory>/logs/devices, one
// folder per device, so it remains directly inspectable alongside server logs.
builder.Services.AddSingleton(services =>
{
    var serverOptions = services.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
    return new ClientLogStore(Path.Combine(serverOptions.DataDirectory, "logs", "devices"));
});
builder.Services.AddSingleton<DeviceKeyStore>();
// Answers "what does the internet see this server as" for the settings page's
// network tab, by asking somebody else - see PublicAddressProbe. Nothing calls
// it until that page is opened.
builder.Services.AddSingleton<PublicAddressProbe>();

// Announces the server on the LAN so it shows up in a client's sidebar without
// anyone typing an address. Registered as a lifecycle service because the port
// it advertises has to be the one Kestrel actually bound - see MdnsAdvertiser.
builder.Services.AddSingleton<MdnsAdvertiser>();
builder.Services.AddHostedService(services => services.GetRequiredService<MdnsAdvertiser>());

// This server's own keypair, loaded above (the https listener needed it before
// the container existed) and registered here. It is not used to sign anything
// outbound - what the server needs is its Fingerprint, which goes into every
// pairing invite so the redeeming device can pin the server's key instead of
// trusting whatever answers at that address. See PairingInvite.
//
// That pin is now load-bearing twice over: the same key is what the server's
// TLS certificate is built from, so a client that redeemed a pairing code can
// validate the https listener with nothing further. See DeviceCertificate.
builder.Services.AddSingleton(new DeviceSigningKey(deviceKey, devicePublicKeyRaw));

var app = builder.Build();

// Which build this is and when it was built, first - see AppVersion.
app.Logger.LogInformation("Flower.Server starting: {Build}", AppVersion.StartupDescription);
app.Logger.LogInformation("Data directory: {DataDirectory}", dataDirectory);
app.Logger.LogInformation("Logging to: {LogFile}", logFile);

// Where to open the web UI, on every start rather than only while a pairing
// code is being printed - an operator on a headless box has no other way to
// learn it. Not a credential, so through the logger like everything else.
{
    var serverOptions = app.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
    if (WebUiHosting.Resolve(app.Environment, serverOptions) != null)
    {
        var origins = WebUiHosting.BrowserOrigins(serverOptions, LocalOrigin(builder.Configuration["Urls"], "localhost:4533"));
        app.Logger.LogInformation(
            "Web interface: {WebUiAddresses}{CertificateNote}",
            string.Join(", ", origins.Select(origin => origin + OnThisMachine(origin))),
            origins.Any(origin => IsOwnTls(origin, serverOptions.HttpsPort)) ? " - https uses this server's own certificate, so a browser warns once" : "");
    }
}

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

        // The same code again, addressed at a browser. A tab pairs itself with
        // its own WebCrypto keypair (see BrowserPeerCredentials), so the web UI
        // needs no separate bootstrap credential any more - it needs this code,
        // in a form a browser can be opened at. Printed under exactly the same
        // gate as the invite above, never on an ordinary boot: it is a live
        // credential, which is also why it goes to stdout rather than through
        // the ILogger.
        //
        // One link per address a browser can pair at - see
        // WebUiHosting.BrowserOrigins for which those are and why only https
        // ones from another machine. This used to be the localhost link alone,
        // which is the one address nobody can open on a headless server - and
        // a headless server is what this mostly runs on.
        var browserOrigins = WebUiHosting.BrowserOrigins(serverOptions, LocalOrigin(builder.Configuration["Urls"], "localhost:4533"));
        Console.WriteLine();
        Console.WriteLine("  Or set it up in a browser (same code, valid just as long):");
        foreach (var origin in browserOrigins)
            Console.WriteLine($"  {WebUiHosting.BuildBrowserPairingUrl(origin, code)}{OnThisMachine(origin)}");
        if (browserOrigins.Any(origin => IsOwnTls(origin, serverOptions.HttpsPort)))
            Console.WriteLine("  (An https address uses this server's own certificate, so the browser warns once - continue past it.)");
        else if (browserOrigins.Count == 1)
            Console.WriteLine("  (TLS is off - Flower:HttpsPort is 0 - so only a browser on this machine can pair: see docs/SELF-HOSTING.md.)");
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
var trustedProxyNetworks = new List<System.Net.IPNetwork>();
foreach (var cidr in trustedProxies)
{
    // System.Net's parser is strict about the address being the network's
    // base one, so 192.168.1.5/24 is rejected rather than quietly read as
    // 192.168.1.0/24 - hence the warning naming both ways this can fail.
    if (System.Net.IPNetwork.TryParse(cidr, out var parsed))
        trustedProxyNetworks.Add(parsed);
    else
        app.Logger.LogWarning(
            "Ignoring {Cidr} in TrustedProxies: not a CIDR, or not written as the network's base address (192.168.1.0/24, not 192.168.1.5/24). Nothing forwarded by it will be believed",
            cidr);
}

if (trustedProxyNetworks.Count > 0)
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
        ForwardLimit = trustedProxyNetworks.Count,
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    foreach (var network in trustedProxyNetworks)
    {
        forwarded.KnownIPNetworks.Add(network);
    }

    app.UseForwardedHeaders(forwarded);
    app.Logger.LogInformation(
        "Believing X-Forwarded-For from {ProxyCount} configured proxy network(s): {Proxies}",
        forwarded.KnownIPNetworks.Count, string.Join(", ", trustedProxies));
}

// Said once, loudly, at the only moment an operator is reading the console: a
// server that answers the open internet should never be one that got there by
// accident. The second warning is the failure this pairs with most often -
// public access through a tunnel, with nothing declaring the tunnel, which
// leaves every remote listener sharing one address and therefore one budget.
var publicAccess = app.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value.AllowPublicAccess;
if (publicAccess)
{
    app.Logger.LogWarning(
        "Flower:AllowPublicAccess is on, so this server answers callers from any address - the LAN allow-list is off. "
        + "Every route that matters still requires a paired device's signature, and that is now the only thing that does. "
        + "See docs/SELF-HOSTING.md.");

    if (trustedProxyNetworks.Count == 0)
    {
        app.Logger.LogWarning(
            "Flower:AllowPublicAccess is on but Flower:TrustedProxies is empty. If a tunnel or reverse proxy is what "
            + "makes this server reachable, name it there - for cloudflared on this machine that is 127.0.0.1/32 - or "
            + "every remote listener arrives as that proxy and shares one rate-limit bucket.");
    }
}

// Two ways the address this server hands out can be wrong, both of which fail
// entirely on the client and leave nothing here to find. A client discards an
// address it cannot use before it dials it, so the server's own log is silent
// and the symptom is "unreachable" with no cause anywhere - which is the whole
// reason these are worth saying at startup, where somebody is looking.
{
    var advertised = app.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value.AdvertisedHost;

    // A .mdns name is the tempting value here - it survives a DHCP move, which
    // is the very problem an operator is usually trying to solve - and it is
    // the one that cannot work. A client resolves a remembered address with
    // Dns.GetHostAddressesAsync (NetworkDiscoveryService) and drops it when
    // that returns nothing; on iOS a .local name is resolved by mDNSResponder
    // rather than through that path, so the address is discarded in silence.
    if (!string.IsNullOrWhiteSpace(advertised)
        && ServerAddressAdvice.LooksLikeMulticastDnsName(advertised))
    {
        app.Logger.LogWarning(
            "Flower:AdvertisedHost is {AdvertisedHost}, an mDNS (.local) name. Clients resolve a remembered address "
            + "through the system resolver, which on iOS does not answer for .local - the address is dropped without "
            + "being tried and the server reads as unreachable with nothing logged here. Use an IP address, or a name "
            + "that ordinary DNS answers for.",
            advertised);
    }

    // Bridge networking in a container: every address this process can see
    // belongs to the container, answers from nowhere else, and is the whole of
    // what /info offers. Deliberately narrow - a host-networked container sees
    // the real LAN address too, so the "all of them" test is what keeps this
    // quiet there - and worded as a suspicion, since a machine genuinely on a
    // 172.16/12 LAN is indistinguishable from here.
    if (string.IsNullOrWhiteSpace(advertised) && ServerAddressAdvice.LooksContainerised())
    {
        var own = LocalAddresses.Own();
        if (own.Count > 0 && own.TrueForAll(ServerAddressAdvice.IsDockerBridgeAddress))
        {
            app.Logger.LogWarning(
                "Every address this server can see of itself is {Addresses}, which looks like Docker bridge networking. "
                + "Those are reachable only from inside the container, and they are all that /info offers a client - so "
                + "a device pairs and then cannot get back. Set Flower:AdvertisedHost to an address clients can dial, or "
                + "use host networking. See docs/SELF-HOSTING.md.",
                string.Join(", ", own));
        }
    }
}

// Behind UseForwardedHeaders, so RemoteIpAddress is already whatever a trusted
// hop said it was - which is what makes "still not a trusted proxy" mean an
// undeclared one. A hop that was believed also consumes its entry from the
// header, so the fully-configured single-hop deployment sees no header left
// here and says nothing. A chain longer than ForwardLimit does leave entries
// behind, and that warns - correctly: the address being rate-limited is then a
// middle hop rather than the client, and naming the extra hop is the fix.
// See ProxyHeaderAudit for why this warns rather than refuses.
var proxyAudit = new ProxyHeaderAudit(trustedProxyNetworks);
app.Use(async (context, next) =>
{
    if (proxyAudit.ShouldWarn(
            context.Connection.RemoteIpAddress,
            context.Request.Headers.ContainsKey("X-Forwarded-For"),
            DateTimeOffset.UtcNow))
    {
        app.Logger.LogWarning(
            proxyAudit.HasTrustedProxies
                ? "An X-Forwarded-For arrived from {RemoteAddress}, which no CIDR in TrustedProxies covers, so it is being ignored. If that address is your proxy or tunnel, add it to Flower:TrustedProxies - until you do, every client behind it shares one address, one rate-limit bucket, and one pass through the LanGuard allow-list. (Repeats at most every {Minutes} minutes.)"
                : "An X-Forwarded-For arrived from {RemoteAddress} but Flower:TrustedProxies is empty, so it is being ignored. If you are running a tunnel or reverse proxy, name it there - for cloudflared on this machine that is 127.0.0.1/32. Until you do, every client behind it shares one address, one rate-limit bucket, and one pass through the LanGuard allow-list. (Repeats at most every {Minutes} minutes.)",
            context.Connection.RemoteIpAddress,
            ProxyHeaderAudit.RepeatInterval.TotalMinutes);
    }

    await next(context);
});

app.Use(async (context, next) =>
{
    // Monitor, not IOptions: the allow-list is editable from the admin API, and
    // IOptions binds once for the life of the process - a CIDR added in the
    // browser would then not apply until a restart, which is exactly the setting
    // an operator is most likely to be changing *because* they are locked out.
    var serverOptions = context.RequestServices.GetRequiredService<IOptionsMonitor<FlowerServerOptions>>().CurrentValue;
    var remoteAddress = context.Connection.RemoteIpAddress;

    // A connection with no address at all is refused either way: every rate
    // limiter behind this keys on one, so admitting it would be admitting a
    // caller nothing downstream can budget.
    if (remoteAddress == null)
    {
        context.Abort();
        return;
    }

    // AllowPublicAccess is what a deployment behind a tunnel or a mapped port
    // sets, and it retires this gate rather than widening it - see the option's
    // own remarks for what is left holding the door, and Program.cs's startup
    // warning, which says the same thing where an operator will actually read
    // it.
    if (!serverOptions.AllowPublicAccess
        && !LanGuard.IsPrivateOrLoopback(remoteAddress, serverOptions.AllowedCidrs, serverOptions.TrustTailscaleRange))
    {
        // Dropped rather than answered 403. A refusal is still a reply, and a
        // reply to an address that was never allowed to be here confirms there
        // is something listening on this port worth coming back to - which is
        // the one thing a server whose door is shut has to gain by saying
        // nothing. Nobody legitimate ever sees this: a client that belongs on
        // this network is inside the allow-list by definition.
        //
        // Logged at Debug so an operator debugging "my phone cannot reach it
        // from the coffee shop" has something to find, without a scanned port
        // filling the Logs tab at Information.
        app.Logger.LogDebug(
            "Dropped a request from {RemoteAddress}: it is outside the allowed networks and public access is off.",
            remoteAddress);
        context.Abort();
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
    scope.ServiceProvider.GetRequiredService<LibraryImportService>().LoadStored();
}

// The rescan is started, not awaited, so the listener opens on the stored
// catalog instead of after a full scan of the library folders - which is what
// LoadStored's own comment has always said this does. Awaiting it meant a
// 16k-track NAS share spent 6m35s refusing connections at every single start,
// with the container reporting healthy throughout and nothing in the log after
// "Loaded 0 stored track(s)" to say why. It also outlived the admin pairing
// code printed above, which is issued before this and expires ten minutes
// later: on a first run, the one credential that can adopt the server could be
// dead before the server would accept it.
//
// Through the coordinator rather than a bare Task.Run, because it already owns
// the three things this needs: a DI scope of its own (LibraryImportService is
// scoped, and the request scope that used to be borrowed here is gone by the
// time a scan ends), the "a scan is running" flag the admin API and the
// browser already poll, and the guard against two importers over the same
// folders - which is exactly what an operator pressing Rescan during the
// startup scan would otherwise start.
app.Services.GetRequiredService<LibraryRescanCoordinator>().TryStart(
    // Still after the first rescan, for the reason it always was: the opening
    // pass should run against the real catalog rather than recomputing every
    // smart playlist twice at startup. Nothing is missed by subscribing this
    // late - Start()'s first pass is immediate and sees whatever landed while
    // the scan was running, including plays reported by a device that paired
    // during it, which is newly possible now that the server is listening.
    onCompleted: () => app.Services.GetRequiredService<SmartPlaylistRefresher>().Start());

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
// The plain-http origin a browser on this machine opens, from the configured
// bind address - see ResolveLocalHost.
static string LocalOrigin(string? configuredUrls, string fallbackHost) =>
    $"http://{ResolveLocalHost(configuredUrls) ?? fallbackHost}";

static string OnThisMachine(string origin) =>
    origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) ? " (on this machine)" : "";

// An https origin on this server's own TLS listener, whose self-signed
// certificate a browser warns about - as opposed to an advertised proxy's
// https://music.example.com, which has a real one and a port of its own.
static bool IsOwnTls(string origin, int httpsPort) =>
    Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Port == httpsPort;

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

// The host portion of the configured bind address, kept exactly as written -
// unlike ResolveLocalHost above, which turns a wildcard into something dialable
// because it is building a link for a human. This one is building a second
// bind address, where a wildcard is the answer rather than the problem.
static string? ResolveBindHost(string? configuredUrls)
{
    var first = configuredUrls?.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    if (first == null || !Uri.TryCreate(first.Trim(), UriKind.Absolute, out var uri))
        return null;

    // Uri.Host strips the brackets an IPv6 literal needs to sit in a URL, and a
    // bind address is going straight back into one.
    return uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.Host}]" : uri.Host;
}

// Exposed so Flower.Server.Tests's WebApplicationFactory<Program> can boot the
// real app in-process. Top-level statements otherwise compile to an internal
// Program class the test project cannot name.
public partial class Program;
