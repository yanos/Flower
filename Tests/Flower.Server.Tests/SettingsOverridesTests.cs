using Microsoft.Extensions.Configuration;

using Flower.Server.Configuration;

namespace Flower.Server.Tests;

// Which settings the page cannot change - see SettingsOverrides. Built from
// the same kinds of provider Program.cs layers, in the same order: the page's
// own flower-server.json, then whatever outranks it.
public sealed class SettingsOverridesTests : IDisposable
{
    // Environment variables under a prefix of this test's own, so no other
    // test running alongside ever sees them.
    private readonly string _prefix = $"FLOWERTEST{Guid.NewGuid():N}_";
    private readonly string _directory = Directory.CreateTempSubdirectory("flower-overrides").FullName;

    private string SettingsFile(string alias)
    {
        var path = Path.Combine(_directory, ServerDataDirectory.SettingsFileName);
        File.WriteAllText(path, $$"""{ "Flower": { "Alias": "{{alias}}" } }""");
        return path;
    }

    [Fact]
    public void A_name_only_the_page_sets_is_editable()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(SettingsFile("Kitchen")).Build();

        Assert.Null(SettingsOverrides.OverriddenBy(configuration, nameof(FlowerServerOptions.Alias)));
    }

    // Lambda's case: Flower__Alias in the compose file.
    [Fact]
    public void An_environment_variable_above_the_file_locks_it()
    {
        Environment.SetEnvironmentVariable($"{_prefix}Flower__Alias", "Flower");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(SettingsFile("Kitchen"))
            .AddEnvironmentVariables(_prefix)
            .Build();

        Assert.Equal("the Flower__Alias environment variable",
            SettingsOverrides.OverriddenBy(configuration, nameof(FlowerServerOptions.Alias)));
    }

    [Fact]
    public void A_command_line_option_above_the_file_locks_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(SettingsFile("Kitchen"))
            .AddCommandLine(["--Flower:Alias=Flower"])
            .Build();

        Assert.Equal("the --Flower:Alias command-line option",
            SettingsOverrides.OverriddenBy(configuration, nameof(FlowerServerOptions.Alias)));
    }

    // The Docker image's own: Flower__LibraryPaths__0=/music sets one entry of
    // a list, and a list is the environment's once it sets any of it.
    [Fact]
    public void One_list_entry_in_the_environment_locks_the_whole_list()
    {
        Environment.SetEnvironmentVariable($"{_prefix}Flower__LibraryPaths__0", "/music");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(SettingsFile("Kitchen"))
            .AddEnvironmentVariables(_prefix)
            .Build();

        Assert.Equal("the Flower__LibraryPaths__0 environment variable",
            SettingsOverrides.OverriddenBy(configuration, nameof(FlowerServerOptions.LibraryPaths)));
    }

    // appsettings.json sits below the file, so its default is exactly what the
    // page is there to change.
    [Fact]
    public void A_value_below_the_file_does_not_lock_it()
    {
        Environment.SetEnvironmentVariable($"{_prefix}Flower__Alias", "Flower");
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables(_prefix)
            .AddJsonFile(SettingsFile("Kitchen"))
            .Build();

        Assert.Null(SettingsOverrides.OverriddenBy(configuration, nameof(FlowerServerOptions.Alias)));
    }

    // Other settings set in the environment say nothing about the name.
    [Fact]
    public void Only_the_setting_asked_about_counts()
    {
        Environment.SetEnvironmentVariable($"{_prefix}Flower__AdvertisedHost", "https://music.example.com");
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(SettingsFile("Kitchen"))
            .AddEnvironmentVariables(_prefix)
            .Build();

        Assert.Null(SettingsOverrides.OverriddenBy(configuration, nameof(FlowerServerOptions.Alias)));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable($"{_prefix}Flower__Alias", null);
        Environment.SetEnvironmentVariable($"{_prefix}Flower__AdvertisedHost", null);
        Environment.SetEnvironmentVariable($"{_prefix}Flower__LibraryPaths__0", null);
        Directory.Delete(_directory, recursive: true);
    }
}
