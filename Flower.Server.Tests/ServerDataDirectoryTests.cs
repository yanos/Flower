using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Flower.Persistence;
using Flower.Server.Configuration;

namespace Flower.Server.Tests;

// Where a self-hosted server keeps its data. The default used to be "./data",
// relative to the working directory, so the same install resolved to a
// different library depending on how it was launched.
public class ServerDataDirectoryTests : IDisposable
{
    private readonly string? _previousDataDirectory = PlatformDataDirectory.Current;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flower-datadir-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        PlatformDataDirectory.Current = _previousDataDirectory;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void An_unset_data_directory_resolves_under_the_platform_app_data_location()
    {
        PlatformDataDirectory.Current = _root;

        // A subdirectory, never the app-data root itself: sharing it with a
        // desktop app on the same machine would share device-key.json, and two
        // peers presenting one fingerprint cannot pair.
        Assert.Equal(Path.Combine(_root, "Server"), ServerDataDirectory.Resolve(""));
        Assert.Equal(Path.Combine(_root, "Server"), ServerDataDirectory.Resolve(null));
    }

    [Fact]
    public void A_configured_directory_wins_and_comes_back_absolute()
    {
        PlatformDataDirectory.Current = _root;
        var configured = Path.Combine(_root, "volume");

        Assert.Equal(configured, ServerDataDirectory.Resolve(configured));
        // The container/NAS case is the reason the setting exists at all, and
        // a relative one must not stay relative - it is handed to
        // PlatformDataDirectory.Current, which outlives any working directory.
        Assert.True(Path.IsPathRooted(ServerDataDirectory.Resolve("./data")));
    }

    [Fact]
    public void The_seeded_settings_file_is_valid_json_that_overrides_nothing()
    {
        Directory.CreateDirectory(_root);
        ServerDataDirectory.SeedSettingsFile(_root);

        var path = Path.Combine(_root, ServerDataDirectory.SettingsFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        // Every documented key is underscore-prefixed, so the seeded file binds
        // to nothing. A real "LibraryPaths": [] in here would sit above
        // appsettings.json and silently blank out the paths an operator had
        // configured there - the file is meant to be discoverable, not active.
        var section = document.RootElement.GetProperty(FlowerServerOptions.SectionName);
        Assert.NotEmpty(section.EnumerateObject());
        Assert.All(section.EnumerateObject(), property => Assert.StartsWith("_", property.Name));
    }

    [Fact]
    public void Seeding_never_overwrites_a_file_the_operator_has_edited()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, ServerDataDirectory.SettingsFileName);
        File.WriteAllText(path, """{ "Flower": { "Alias": "Basement NAS" } }""");

        ServerDataDirectory.SeedSettingsFile(_root);

        Assert.Contains("Basement NAS", File.ReadAllText(path));
    }
}

// The settings file in the data directory has to land in exactly one place in
// the configuration chain: above the appsettings.json that ships with the
// binary, below the environment. Appended instead of inserted (the obvious
// way to write it) it would outrank the environment, and a container's
// Flower__* variable - or ASPNETCORE_URLS - would be silently overruled by a
// file sitting on its data volume.
public class ServerSettingsFilePrecedenceTests : IDisposable
{
    private readonly string? _previousDataDirectory = PlatformDataDirectory.Current;
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "flower-settings-tests-" + Guid.NewGuid());

    private sealed class Host(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Flower:DataDirectory", dataDirectory);
            // Scanning is beside the point here, and an empty LibraryPaths
            // makes the importer fall back to the real ~/Music.
            builder.UseSetting("Flower:LibraryPaths:0", Path.Combine(dataDirectory, "empty-library"));
        }
    }

    public void Dispose()
    {
        PlatformDataDirectory.Current = _previousDataDirectory;
        Environment.SetEnvironmentVariable("Flower__Alias", null);
        try { Directory.Delete(_dataDirectory, recursive: true); } catch { /* best effort */ }
    }

    private FlowerServerOptions OptionsFrom(Host host) =>
        host.Services.GetRequiredService<IOptions<FlowerServerOptions>>().Value;

    [Fact]
    public void A_setting_in_the_data_directory_beats_the_shipped_appsettings_file()
    {
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "empty-library"));
        File.WriteAllText(Path.Combine(_dataDirectory, ServerDataDirectory.SettingsFileName),
            """{ "Flower": { "Alias": "Basement NAS" } }""");

        using var host = new Host(_dataDirectory);

        Assert.Equal("Basement NAS", OptionsFrom(host).Alias);
    }

    [Fact]
    public void The_environment_still_beats_the_data_directory_settings_file()
    {
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "empty-library"));
        File.WriteAllText(Path.Combine(_dataDirectory, ServerDataDirectory.SettingsFileName),
            """{ "Flower": { "Alias": "Basement NAS" } }""");
        Environment.SetEnvironmentVariable("Flower__Alias", "From The Container");

        using var host = new Host(_dataDirectory);

        Assert.Equal("From The Container", OptionsFrom(host).Alias);
    }

    [Fact]
    public void The_resolved_data_directory_is_what_the_rest_of_the_app_reads()
    {
        Directory.CreateDirectory(Path.Combine(_dataDirectory, "empty-library"));

        using var host = new Host(_dataDirectory);

        // Program.cs writes the resolved absolute path back into configuration
        // so FlowerDb's path and PlatformDataDirectory.Current cannot disagree.
        Assert.Equal(_dataDirectory, OptionsFrom(host).DataDirectory);
    }
}
