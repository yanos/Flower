using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Persistence;
using Flower.Server.Services;

namespace Flower.Server.Tests;

// A server nobody has ever paired with cannot be administered: pairing codes
// are issued from /api/admin, and /api/admin only admits a device that already
// paired as an admin. Program.cs breaks that circularity by minting one
// admin-granting code itself at startup and printing it, which on a headless
// box means it lands in `docker logs`.
//
// This replaces the old "refuse to boot without a configured admin password"
// check - there is no admin password any more (SYNC-PLAN.md, "Passwordless by
// design"), so the failure mode it guarded against no longer exists.
public class BootstrapPairingCodeTests
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public string DataDirectory { get; } =
            Path.Combine(Path.GetTempPath(), "flower-server-bootstrap-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var emptyLibrary = Path.Combine(DataDirectory, "lib");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(emptyLibrary);

            builder.UseSetting("Flower:DataDirectory", DataDirectory);
            builder.UseSetting("Flower:LibraryPaths:0", emptyLibrary);
            builder.UseSetting("Flower:IntegrateWithITunes", "false");
        }
    }

    [Fact]
    public void A_server_with_no_admin_prints_a_pairing_code_at_startup()
    {
        var previous = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            using var factory = new Factory();
            _ = factory.Services;
        }
        finally
        {
            Console.SetOut(previous);
        }

        var output = captured.ToString();
        Assert.Contains("No device can administer this server yet", output);
        // The invite, not just the bare code: it carries the server's own
        // fingerprint, which is what lets the pairing device pin the key
        // rather than trusting whatever answers at that address.
        Assert.Contains("flower://pair?", output);
        Assert.Contains("fp=", output);
    }

    [Fact]
    public async Task A_server_that_already_has_an_admin_does_not_print_one()
    {
        // Otherwise every restart would broadcast a live admin-granting
        // credential to the logs of an already-configured server.
        var factory = new Factory();
        var previousDataDirectory = PlatformDataDirectory.Current;
        try
        {
            PlatformDataDirectory.Current = factory.DataDirectory;
            Directory.CreateDirectory(factory.DataDirectory);
            await new TrustedPeerStore(NullLogger<TrustedPeerStore>.Instance)
                .ApproveAsync("fingerprint", "Existing admin", "public-key", isAdmin: true);
        }
        finally
        {
            PlatformDataDirectory.Current = previousDataDirectory;
        }

        var previous = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            _ = factory.Services;
        }
        finally
        {
            Console.SetOut(previous);
            factory.Dispose();
        }

        Assert.DoesNotContain("No device can administer this server yet", captured.ToString());
    }
}
