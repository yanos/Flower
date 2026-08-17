using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Flower.Persistence;
using Flower.Server.Configuration;
using Flower.Server.Data;
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

builder.Services.AddDbContextFactory<FlowerDbContext>((services, options) =>
{
    var serverOptions = services.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
    Directory.CreateDirectory(serverOptions.DataDirectory);
    var dbPath = Path.Combine(serverOptions.DataDirectory, "flower.db");
    // Default Timeout is Microsoft.Data.Sqlite's busy-timeout knob (seconds) -
    // EF Core 7+ no longer auto-retries SQLITE_BUSY itself (see SYNC-PLAN.md's
    // "Recommended stack" note), so this is what actually absorbs a writer
    // colliding with another connection under WAL.
    options.UseSqlite($"Data Source={dbPath};Cache=Shared;Default Timeout=30");
});

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
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FlowerDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    // MigrateAsync, not EnsureCreatedAsync: EnsureCreated stamps no schema
    // version, so any later change to an entity left a self-hoster with a
    // silently stale table and no upgrade path but deleting flower.db. Real
    // migrations (Data/Migrations) apply incrementally and are the whole
    // reason the DB can evolve without data loss - add one with
    // `dotnet ef migrations add <Name> -p Flower.Server -s Flower.Server -o Data/Migrations`
    // whenever an entity changes.
    await db.Database.MigrateAsync();
    // WAL requires local storage, not NFS/SMB (see SYNC-PLAN.md) - a pragma,
    // not a connection-string option, and persists in the db file itself once
    // set, but cheap enough to re-issue on every startup rather than track.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

    var importService = scope.ServiceProvider.GetRequiredService<LibraryImportService>();
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
