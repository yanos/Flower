using System.IO;
using System.Text.Json;

using Flower.Persistence;
using Flower.Tests.TestSupport;

using Serilog.Events;

using Xunit;

namespace Flower.Tests;

// AppSettingsStore.ReadLogFileMinimumLevel is the one piece of settings loading
// that runs before anything else in startup - before the logger it configures
// exists, and before the real Load() that would normally report a problem. So
// it has no way to complain, and every failure mode has to resolve to the same
// safe answer instead. That is what these pin down: not the happy path so much
// as "a broken or missing settings file must not decide the log level, and must
// not throw on the first line of App.OnFrameworkInitializationCompleted."
[Collection("PlatformDataDirectory")]
public class LogFileMinimumLevelTests : PinnedDataDirectory
{
    private void WriteSettingsJson(string contents) =>
        File.WriteAllText(AppSettingsStore.StorePath, contents);

    [Fact]
    public void Defaults_to_Debug_when_there_is_no_settings_file()
    {
        // First run. The pinned data directory is empty, so StorePath does not exist.
        Assert.Equal(LogEventLevel.Debug, AppSettingsStore.ReadLogFileMinimumLevel());
    }

    [Fact]
    public void Reads_the_stored_level()
    {
        WriteSettingsJson("""{"LogFileMinimumLevel":"Verbose"}""");

        Assert.Equal(LogEventLevel.Verbose, AppSettingsStore.ReadLogFileMinimumLevel());
    }

    // The whole point of the opt-in: Verbose is what makes the Trace lines
    // (discovery polls, LibVLC callback tracing) reach a sink at all, so a
    // round-trip through the real store has to preserve it.
    [Fact]
    public void Survives_a_round_trip_through_the_store()
    {
        var store = new AppSettingsStore();
        var settings = store.Load();
        settings.LogFileMinimumLevel = LogEventLevel.Verbose;
        store.Save(settings);

        Assert.Equal(LogEventLevel.Verbose, AppSettingsStore.ReadLogFileMinimumLevel());
    }

    [Fact]
    public void Defaults_to_Debug_when_the_setting_is_absent()
    {
        // A settings file written before this key existed - every real user's
        // file, since there are no released users but there are developer ones.
        WriteSettingsJson("""{"LogFontSize":14}""");

        Assert.Equal(LogEventLevel.Debug, AppSettingsStore.ReadLogFileMinimumLevel());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("""{"LogFileMinimumLevel":"Screaming"}""")]
    [InlineData("""{"LogFileMinimumLevel":null}""")]
    [InlineData("""{"LogFileMinimumLevel":7}""")]
    public void Falls_back_to_Debug_rather_than_throwing(string contents)
    {
        // Whatever is wrong here, the real Load() moments later reports it
        // properly - this call must only decline to guess, and must never be
        // the reason startup dies.
        WriteSettingsJson(contents);

        Assert.Equal(LogEventLevel.Debug, AppSettingsStore.ReadLogFileMinimumLevel());
    }

    // The peek deliberately parses the file itself rather than deserializing
    // AppSettings, so it must not care about anything else in there - including
    // values that would fail a full bind.
    [Fact]
    public void Ignores_the_rest_of_the_file()
    {
        WriteSettingsJson("""
            {"LogFileMinimumLevel":"Warning","LibraryPaths":"not an array","ThemePreference":42}
            """);

        Assert.Equal(LogEventLevel.Warning, AppSettingsStore.ReadLogFileMinimumLevel());
    }

    // Reading the level must not have side effects: the real Load() is what may
    // seed library paths and write the file back, and doing any of that this
    // early - before logging, from a method that cannot report it - is exactly
    // what this peek exists to avoid.
    [Fact]
    public void Does_not_create_or_modify_the_settings_file()
    {
        Assert.False(File.Exists(AppSettingsStore.StorePath));

        AppSettingsStore.ReadLogFileMinimumLevel();

        Assert.False(File.Exists(AppSettingsStore.StorePath));
    }

    [Fact]
    public void Does_not_rewrite_an_existing_settings_file()
    {
        WriteSettingsJson("""{"LogFileMinimumLevel":"Error"}""");
        var before = File.ReadAllText(AppSettingsStore.StorePath);

        AppSettingsStore.ReadLogFileMinimumLevel();

        Assert.Equal(before, File.ReadAllText(AppSettingsStore.StorePath));
    }
}
