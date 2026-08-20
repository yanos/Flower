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
builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddSingleton<PairingCodeService>();
builder.Services.AddSingleton<NonceReplayGuard>();
builder.Services.AddSingleton<TrustedPeerStore>();

var app = builder.Build();

// Fail fast rather than boot an open server: AdminPassword guards the admin
// API *and*, via SubsonicAuth, every /rest route, so shipping a usable
// placeholder meant a self-hoster who never edited the config had one
// well-known credential in front of their whole library. Checked after
// Build() so it reads the fully-composed configuration (env vars, user
// secrets, Docker secrets) and not just appsettings.json.
{
    var configured = app.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
    if (string.IsNullOrWhiteSpace(configured.AdminPassword)
        || configured.AdminPassword == FlowerServerOptions.PlaceholderAdminPassword)
    {
        throw new InvalidOperationException(
            "Flower:AdminPassword is unset or still the placeholder. Set a real password before starting the server "
            + "- e.g. Flower__AdminPassword=<password> in the environment, or the Flower:AdminPassword key in appsettings.json. "
            + "It protects both /api/admin and the whole /rest Subsonic API.");
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

app.Run();

// Exposed so Flower.Server.Tests's WebApplicationFactory<Program> can boot the
// real app in-process. Top-level statements otherwise compile to an internal
// Program class the test project cannot name.
public partial class Program;
