using System;
using System.Threading;
using System.Threading.Tasks;

using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

// When a desktop installed from a release looks for a newer one, and what it
// says - AppUpdatesViewModel. Where the update comes from is the head's
// (Velopack, or GitHub for the dmg); this holds the shared half: once a day
// across launches, never with the setting off, a failure tried again rather
// than counted as a check, and an update found offered rather than installed.
public class AppUpdatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeUpdate(string version) : AppUpdate(version)
    {
        public int Installs { get; private set; }
        public override string InstallLabel => "Restart to Update";
        public override string Description => $"Flower {Version} is ready.";
        public override void Install() => Installs++;
    }

    private sealed class FakeUpdater : IAppUpdater
    {
        public AppUpdate? Next { get; set; }
        public Exception? Failure { get; set; }
        public int Checks { get; private set; }

        public Task<AppUpdate?> FindAsync(CancellationToken ct = default)
        {
            Checks++;
            return Failure is { } failure ? Task.FromException<AppUpdate?>(failure) : Task.FromResult(Next);
        }
    }

    private static AppUpdatesViewModel Updates(FakeUpdater updater, AppSettings settings) =>
        new(updater, settings, () => Task.CompletedTask, NullLogger<AppUpdatesViewModel>.Instance, new FixedClock());

    [Fact]
    public async Task A_first_launch_checks_and_remembers_when()
    {
        var updater = new FakeUpdater();
        var settings = new AppSettings();

        await Updates(updater, settings).CheckIfDueAsync();

        Assert.Equal(1, updater.Checks);
        Assert.Equal(Now, settings.LastUpdateCheck);
    }

    [Fact]
    public async Task A_check_within_the_day_is_not_repeated_even_across_a_relaunch()
    {
        var updater = new FakeUpdater();
        var settings = new AppSettings { LastUpdateCheck = Now.AddHours(-23) };

        await Updates(updater, settings).CheckIfDueAsync();
        Assert.Equal(0, updater.Checks);

        settings.LastUpdateCheck = Now.AddHours(-24);
        await Updates(updater, settings).CheckIfDueAsync();
        Assert.Equal(1, updater.Checks);
    }

    [Fact]
    public async Task Turned_off_it_never_checks_on_its_own_but_still_checks_when_asked()
    {
        var updater = new FakeUpdater();
        var updates = Updates(updater, new AppSettings { CheckForUpdatesAutomatically = false });

        await updates.CheckIfDueAsync();
        Assert.Equal(0, updater.Checks);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)updates.CheckNowCommand).ExecuteAsync(null);
        Assert.Equal(1, updater.Checks);
    }

    [Fact]
    public async Task A_failed_check_is_tried_again_at_the_next_poll_rather_than_tomorrow()
    {
        var updater = new FakeUpdater { Failure = new System.Net.Http.HttpRequestException("offline") };
        var settings = new AppSettings { LastUpdateCheck = Now.AddDays(-2) };
        var updates = Updates(updater, settings);

        await updates.CheckIfDueAsync();

        Assert.Equal(Now.AddDays(-2), settings.LastUpdateCheck);
        Assert.True(updates.IsDue);
        Assert.Null(updates.Available);
    }

    [Fact]
    public async Task Only_a_check_asked_for_says_why_it_failed()
    {
        var updater = new FakeUpdater { Failure = new System.Net.Http.HttpRequestException("offline") };
        var updates = Updates(updater, new AppSettings());

        await updates.CheckIfDueAsync();
        Assert.DoesNotContain("offline", updates.Status);

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)updates.CheckNowCommand).ExecuteAsync(null);
        Assert.Contains("offline", updates.Status);
    }

    [Fact]
    public async Task An_update_found_is_offered_not_installed_and_nothing_looks_again()
    {
        var update = new FakeUpdate("0.5.0");
        var updater = new FakeUpdater { Next = update };
        var settings = new AppSettings();
        var updates = Updates(updater, settings);

        await updates.CheckIfDueAsync();

        Assert.Same(update, updates.Available);
        Assert.True(updates.ShowsBanner);
        Assert.Equal(0, update.Installs);

        settings.LastUpdateCheck = Now.AddDays(-2);
        await updates.CheckIfDueAsync();
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)updates.CheckNowCommand).ExecuteAsync(null);
        Assert.Equal(1, updater.Checks);

        updates.InstallCommand.Execute(null);
        Assert.Equal(1, update.Installs);
    }

    [Fact]
    public async Task Dismissing_the_banner_leaves_the_update_on_offer_in_settings()
    {
        var updates = Updates(new FakeUpdater { Next = new FakeUpdate("0.5.0") }, new AppSettings());
        await updates.CheckIfDueAsync();

        updates.DismissCommand.Execute(null);

        Assert.False(updates.ShowsBanner);
        Assert.NotNull(updates.Available);
    }
}
