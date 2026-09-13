using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Logging;
using Flower.Persistence;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Serilog.Events;

using Xunit;

namespace Flower.Tests;

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
// parallel. [AvaloniaFact] because the entry flush lands back on the UI thread
// through Dispatcher.UIThread.Post.
[Collection("PlatformDataDirectory")]
public class LogViewModelTests : PinnedDataDirectory
{
    private readonly string _marker = "marker-" + Guid.NewGuid().ToString("N");

    private static LogViewModel Make(AppSettings? settings = null) =>
        new(InMemoryLogStore.Instance, settings ?? new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance));

    // Logs one entry to the shared local store, tagged so this test can find it.
    private void LogLocal(string message, string level = "Information", string? source = null) =>
        InMemoryLogStore.Instance.Add(
            new InMemoryLogEntry(DateTimeOffset.Now, level, source, $"{_marker} {message}", null));

    // Drains the dispatcher until `condition` holds. Deliberately RunJobs and
    // not Dispatcher.UIThread.MainLoop: the headless session owns the
    // dispatcher thread, so a test that parks it in MainLoop holds up every
    // [AvaloniaFact] queued behind it. Everything this class waits on is a
    // plain Dispatcher.Post, so draining is enough.
    private static void PumpUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "the expected dispatcher work never completed");
    }

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

    // An entry whose level string is not a LogEventLevel at all is dropped
    // rather than shown unconditionally.
    [Fact]
    public void An_unparseable_level_is_filtered_out()
    {
        LogLocal("bogus level line", level: "NotALevel");
        var vm = Make();

        vm.FilterText = _marker;

        Assert.Empty(vm.DisplayLines);
    }

    [Fact]
    public void MinimumLevel_and_FontSize_and_word_wrap_are_restored_from_settings()
    {
        var vm = Make(new AppSettings
        {
            LogMinimumLevel = LogEventLevel.Error, LogFontSize = 18, LogWordWrapEnabled = true,
        });

        Assert.Equal(LogEventLevel.Error, vm.MinimumLevel);
        Assert.Equal(18, vm.FontSize);
        Assert.True(vm.IsWordWrapEnabled);
    }

    // ── LinesReset's "did it actually grow" argument ──────────────────────────

    // The View scrolls on this flag, so a level/filter change re-rendering the
    // same underlying log must report false or the pane yanks around under the
    // user every time they touch the filter box.
    [Fact]
    public void A_filter_or_level_change_reports_no_growth()
    {
        LogLocal("a line");
        var vm = Make();
        vm.FilterText = _marker;

        var grew = new List<bool>();
        vm.LinesReset += (_, g) => grew.Add(g);

        vm.FilterText = _marker + " a";
        vm.MinimumLevel = LogEventLevel.Debug;
        vm.FilterText = _marker;

        Assert.NotEmpty(grew);
        Assert.All(grew, g => Assert.False(g));
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

    // ── Live entries ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void A_new_entry_is_appended_rather_than_re_rendering_the_document()
    {
        var vm = Make();
        vm.FilterText = _marker;

        var appended = new List<IReadOnlyList<string>>();
        var resets = 0;
        vm.LinesAppended += (_, batch) => appended.Add(batch);
        vm.LinesReset += (_, _) => resets++;

        LogLocal("live line");
        PumpUntil(() => appended.Count > 0);

        Assert.Equal(0, resets);
        Assert.Contains("live line", Assert.Single(Assert.Single(appended)));
        Assert.Contains(vm.DisplayLines, l => l.Contains("live line"));
    }

    // The whole point of the pending-entry buffer: a burst becomes a handful of
    // LinesAppended events (and so a handful of TextEditor.AppendText calls),
    // not one per line. Note this pins the drain-all batching in
    // FlushPendingEntries, not the _flushScheduled flag - that flag only
    // suppresses redundant dispatcher posts, which have no effect on the events
    // this class emits and so cannot be observed from out here.
    [AvaloniaFact]
    public void A_burst_of_entries_is_coalesced_into_fewer_batches_than_lines()
    {
        var vm = Make();
        vm.FilterText = _marker;

        var batches = new List<IReadOnlyList<string>>();
        vm.LinesAppended += (_, batch) => batches.Add(batch);

        for (var i = 0; i < 40; i++)
            LogLocal($"burst line {i}");

        PumpUntil(() => batches.Sum(b => b.Count) == 40);
        var batchesForTheBurst = batches.Count;

        // A second wave after the first flush has already run: each flush has
        // to consume what it appended, or these carry the first 40 along with
        // them and every line lands in the editor twice.
        for (var i = 0; i < 5; i++)
            LogLocal($"second wave {i}");
        PumpUntil(() => batches.Sum(b => b.Count) >= 45);

        Assert.Equal(45, batches.Sum(b => b.Count));
        Assert.Equal(45, batches.SelectMany(b => b).Distinct().Count());
        Assert.True(batchesForTheBurst < 40,
                    $"expected coalescing, got {batchesForTheBurst} batches for 40 lines");
    }

    // A live entry that the current filter excludes updates the backing entry
    // list but must not reach the View.
    [AvaloniaFact]
    public void A_filtered_out_entry_appends_nothing()
    {
        var vm = Make();
        vm.FilterText = _marker + " keep";

        var appended = new List<IReadOnlyList<string>>();
        vm.LinesAppended += (_, batch) => appended.Add(batch);

        LogLocal("drop this one");
        LogLocal("keep this one");
        PumpUntil(() => appended.Count > 0);

        var lines = appended.SelectMany(b => b).ToList();
        Assert.Contains(lines, l => l.Contains("keep this one"));
        Assert.DoesNotContain(lines, l => l.Contains("drop this one"));
    }
}
