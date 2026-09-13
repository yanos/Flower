using Flower.Server.Configuration;

using Microsoft.Extensions.Configuration;

using Serilog.Events;

using Xunit;

namespace Flower.Server.Tests;

// The Logging:LogLevel section spent its whole life doing nothing - AddSerilog
// outranks the filters it feeds - so what these pin down is that the section
// now arrives somewhere it is honoured, with its ordinary ASP.NET spelling
// intact. The shipped appsettings.json case is the one that matters most: a
// Default of Information with Flower lowered to Debug underneath it.
public class LogLevelSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>($"Logging:LogLevel:{e.Key}", e.Value)))
            .Build();

    [Fact]
    public void Default_becomes_the_floor()
    {
        var (floor, overrides) = LogLevelSettings.Read(Config(("Default", "Warning")));

        Assert.Equal(LogEventLevel.Warning, floor);
        Assert.Empty(overrides);
    }

    [Fact]
    public void An_absent_section_leaves_the_floor_at_Debug()
    {
        var (floor, overrides) = LogLevelSettings.Read(new ConfigurationBuilder().Build());

        Assert.Equal(LogEventLevel.Debug, floor);
        Assert.Empty(overrides);
    }

    [Fact]
    public void Every_other_key_becomes_a_category_override()
    {
        var (_, overrides) = LogLevelSettings.Read(
            Config(("Default", "Information"), ("Flower", "Debug"), ("Microsoft", "Warning")));

        Assert.Equal(LogEventLevel.Debug, overrides["Flower"]);
        Assert.Equal(LogEventLevel.Warning, overrides["Microsoft"]);
        Assert.False(overrides.ContainsKey("Default"));
    }

    // An override below the floor is the shipped configuration, not an edge
    // case: it is how this server's own Debug lines survive a Default that
    // silences the framework's.
    [Fact]
    public void An_override_may_sit_below_the_floor()
    {
        var (floor, overrides) = LogLevelSettings.Read(
            Config(("Default", "Information"), ("Flower", "Trace")));

        Assert.Equal(LogEventLevel.Information, floor);
        Assert.Equal(LogEventLevel.Verbose, overrides["Flower"]);
    }

    // The file documents its own keys with _-prefixed siblings; those are prose.
    [Fact]
    public void Documentation_keys_are_not_categories()
    {
        var (_, overrides) = LogLevelSettings.Read(
            Config(("_Flower", "some prose about what this section does"), ("Flower", "Debug")));

        Assert.Single(overrides);
        Assert.Equal(LogEventLevel.Debug, overrides["Flower"]);
    }

    [Fact]
    public void An_unparseable_level_is_ignored_rather_than_throwing()
    {
        var (floor, overrides) = LogLevelSettings.Read(
            Config(("Default", "Loud"), ("Flower", "Debug")));

        Assert.Equal(LogEventLevel.Debug, floor);
        Assert.Single(overrides);
    }

    [Theory]
    [InlineData("Trace", LogEventLevel.Verbose)]
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("Debug", LogEventLevel.Debug)]
    [InlineData("information", LogEventLevel.Information)]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("Error", LogEventLevel.Error)]
    [InlineData("Critical", LogEventLevel.Fatal)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    public void Both_vocabularies_parse(string value, LogEventLevel expected)
    {
        Assert.True(LogLevelSettings.TryParse(value, out var level));
        Assert.Equal(expected, level);
    }

    // Serilog has no "off", so None has to become a level nothing can reach.
    [Fact]
    public void None_turns_a_category_off()
    {
        Assert.True(LogLevelSettings.TryParse("None", out var level));
        Assert.Equal(LogLevelSettings.Off, level);
        Assert.True(level > LogEventLevel.Fatal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_values_are_not_levels(string? value)
    {
        Assert.False(LogLevelSettings.TryParse(value, out _));
    }
}
