using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Flower.Logging;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Serilog.Events;

using Xunit;

namespace Flower.Tests.Logging;

// docs/ARCHITECTURE-REVIEW.md §5.8. LogViewModel is the Log window's view of
// this device's own live log - and only that one: the rows for the paired
// server and its other devices now live on the server's own settings screen
// (see SettingsLogTabTests). Its contract with the View is two events rather
// than a bindable collection: LinesReset (replace the document) and
// LinesAppended (append one coalesced batch). Both are asserted here directly.
//
// InMemoryLogStore is a process-wide singleton with a private constructor, so
// there is no fresh instance to hand this ViewModel - every test therefore
// tags its entries with a unique marker and sets FilterText to it, which
// isolates DisplayLines from whatever the rest of the suite is logging in
// parallel. LogDispatcherTests covers live entry flushing through the real
// dispatcher; these tests cover filtering and reload notifications.
[Collection("PlatformDataDirectory")]
public class LogViewModelTests : PinnedDataDirectory
{
    private readonly string _marker = "marker-" + Guid.NewGuid().ToString("N");

    private LogViewModel Make(AppSettings? settings = null) =>
        Own(new LogViewModel(InMemoryLogStore.Instance, settings ?? new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance)));

    // Logs one entry to the shared local store, tagged so this test can find it.
    private void LogLocal(string message, string level = "Information", string? source = null) =>
        InMemoryLogStore.Instance.Add(
            new InMemoryLogEntry(DateTimeOffset.Now, level, source, $"{_marker} {message}", null));

    // ── Filtering ─────────────────────────────────────────────────────────────

    [Fact]
    public void FilterText_matches_the_message_case_insensitively()
    {
        LogLocal("Alpha Event");
        LogLocal("Beta Event");
        var vm = Make();

        vm.FilterText = _marker;
        var both = vm.DisplayLines.Count;

        vm.FilterText = "alpha";
        Assert.Equal(2, both);
        Assert.Contains("Alpha Event", Assert.Single(vm.DisplayLines));
    }

    [Fact]
    public void FilterText_also_matches_the_source_context()
    {
        LogLocal("nothing distinctive here", source: _marker + ".SomeService");
        var vm = Make();

        vm.FilterText = _marker + ".SomeService";

        Assert.NotEmpty(vm.DisplayLines);
    }

    [Fact]
    public void MinimumLevel_hides_everything_below_it()
    {
        LogLocal("a debug line", level: "Debug");
        LogLocal("a warning line", level: "Warning");
        var vm = Make();
        vm.FilterText = _marker;

        vm.MinimumLevel = LogEventLevel.Verbose;
        Assert.Equal(2, vm.DisplayLines.Count);

        vm.MinimumLevel = LogEventLevel.Warning;
        Assert.Single(vm.DisplayLines);
        Assert.Contains("a warning line", vm.DisplayLines[0]);
    }

    // Reloading is what reopening the window does, and the freshly attached
    // view has to be scrolled to the end of what it just painted - so a reload
    // counts as growth even though it is measured against a log that already
    // had at least as many lines in it.
    [Fact]
    public void Reloading_reports_growth_so_the_reopened_window_scrolls_to_the_end()
    {
        LogLocal("a line");
        var vm = Make();
        vm.FilterText = _marker;

        bool? grew = null;
        vm.LinesReset += (_, g) => grew = g;
        vm.Reload();

        Assert.True(grew);
    }
}
