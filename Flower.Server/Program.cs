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

app.Use(async (context, next) =>
{
    var serverOptions = context.RequestServices.GetRequiredService<IOptions<FlowerServerOptions>>().Value;
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress == null || !LanGuard.IsPrivateOrLoopback(remoteAddress, serverOptions.AllowedCidrs))
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
    await db.Database.EnsureCreatedAsync();
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
