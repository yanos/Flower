using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Logging;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Serilog.Events;

using Xunit;

namespace Flower.Tests;

// docs/ARCHITECTURE-REVIEW.md §5.8. LogViewModel is the "This Device live log
// vs. a log read off the paired server" selector behind the Log window, and
// its whole contract with the View is two events rather than a bindable
// collection: LinesReset (replace the document) and LinesAppended (append one
// coalesced batch). Both are asserted here directly.
//
// InMemoryLogStore is a process-wide singleton with a private constructor, so
// there is no fresh instance to hand this ViewModel - every test therefore
// tags its entries with a unique marker and sets FilterText to it, which
// isolates DisplayLines from whatever the rest of the suite is logging in
// parallel. [AvaloniaFact] because the local-entry flush and every remote
// fetch land back on the UI thread through Dispatcher.UIThread.Post.
[Collection("PlatformDataDirectory")]
public class LogViewModelTests : PinnedDataDirectory
{
    private readonly FakeRemoteLogSource _remote = new();
    private readonly DeviceNicknameStore _nicknameStore = new(NullLogger<DeviceNicknameStore>.Instance);
    private readonly string _marker = "marker-" + Guid.NewGuid().ToString("N");

    private LogViewModel Make(AppSettings? settings = null) =>
        new(InMemoryLogStore.Instance, settings ?? new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            _nicknameStore, _remote);

    // The roster arrives asynchronously, so a test that wants the extra rows
    // has to let the fetch land before asserting on SidebarItems.
    private LogViewModel MakeWithRoster(params string[] fingerprints)
    {
        _remote.Devices = fingerprints.Select(f => new RemoteDevice(f, f.ToUpperInvariant())).ToList();
        var vm = Make();
        PumpUntil(() => vm.SidebarItems.Count == fingerprints.Length + 2);
        return vm;
    }

    // Selecting a remote row starts a fetch; the pane fills when it lands.
    private static void Select(LogViewModel vm, string fingerprint)
    {
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == fingerprint);
        PumpUntil(() => !vm.DisplayLines.SequenceEqual(new[] { "(loading...)" }));
    }

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

    // Drains for a fixed span, asserting nothing - for "this must NOT happen".
    private static void Drain(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    // ── Sidebar / selection ───────────────────────────────────────────────────

    // "This Device" is always index 0 and is what a fresh window lands on.
    [Fact]
    public void It_starts_on_This_Device()
    {
        var vm = Make();

        Assert.NotEmpty(vm.SidebarItems);
        Assert.Equal(LogSidebarItemKind.ThisDevice, vm.SidebarItems[0].Kind);
        Assert.Same(vm.SidebarItems[0], vm.SelectedSidebarItem);
    }

    // With no paired server there is no roster, so the window is just the one
    // row - the same answer an unreachable server gives.
    [AvaloniaFact]
    public void With_no_server_roster_only_This_Device_is_listed()
    {
        _remote.Devices = null;

        Assert.Single(Make().SidebarItems);
    }

    [AvaloniaFact]
    public void A_roster_adds_the_server_and_one_row_per_device()
    {
        var vm = MakeWithRoster("fp-1");

        Assert.Equal(3, vm.SidebarItems.Count);
        Assert.Equal(LogSidebarItemKind.ThisDevice, vm.SidebarItems[0].Kind);
        Assert.Equal(LogSidebarItemKind.Server, vm.SidebarItems[1].Kind);
        Assert.Equal("fp-1", vm.SidebarItems[2].Fingerprint);
    }

    // RefreshSidebarItems runs again every time the window is reopened, since
    // the server admitting a new device has no live notification of its own.
    [AvaloniaFact]
    public void Refreshing_the_sidebar_keeps_the_current_selection_when_it_still_exists()
    {
        var vm = MakeWithRoster("fp-1");
        Select(vm, "fp-1");

        _remote.Devices!.Add(new RemoteDevice("fp-2", "Another Phone"));
        vm.RefreshSidebarItems();
        PumpUntil(() => vm.SidebarItems.Count == 4);

        Assert.Equal("fp-1", vm.SelectedSidebarItem!.Fingerprint);
    }

    // A revoked device's row is gone, so the selection has to fall back rather
    // than dangle.
    [AvaloniaFact]
    public void Refreshing_the_sidebar_falls_back_to_This_Device_when_the_selection_vanished()
    {
        var vm = MakeWithRoster("fp-1");
        Select(vm, "fp-1");

        _remote.Devices!.Clear();
        vm.RefreshSidebarItems();
        PumpUntil(() => vm.SidebarItems.Count == 1);

        Assert.Equal(LogSidebarItemKind.ThisDevice, vm.SelectedSidebarItem!.Kind);
    }

    // A device that has never pushed anything gets an explicit placeholder, not
    // a blank pane indistinguishable from "connected but silent".
    [AvaloniaFact]
    public void A_device_with_no_snapshot_yet_shows_a_placeholder_line()
    {
        var vm = MakeWithRoster("fp-1");

        Select(vm, "fp-1");

        Assert.Equal(new[] { "(no log snapshot received from this device yet)" }, vm.DisplayLines.ToArray());
    }

    // Distinct from the placeholder above: "the server would not tell us" is a
    // different thing from "that phone has not pushed yet", and reading one as
    // the other sends the user looking in the wrong place.
    [AvaloniaFact]
    public void A_log_the_server_will_not_serve_says_so_rather_than_looking_empty()
    {
        var vm = MakeWithRoster("fp-1");
        _remote.ServerLog = null;

        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Kind == LogSidebarItemKind.Server);
        PumpUntil(() => vm.DisplayLines.Count == 1 && vm.DisplayLines[0].StartsWith("(could not read"));

        Assert.Contains("unreachable", Assert.Single(vm.DisplayLines));
    }

    [AvaloniaFact]
    public void Selecting_a_device_shows_its_pushed_snapshot()
    {
        var vm = MakeWithRoster("fp-1");
        _remote.DeviceLogs["fp-1"] = FakeRemoteLogSource.Lines("client line one", "client line two");

        Select(vm, "fp-1");

        Assert.Equal(2, vm.DisplayLines.Count);
        Assert.Contains("client line one", vm.DisplayLines[0]);
        Assert.Contains("client line two", vm.DisplayLines[1]);
    }

    [AvaloniaFact]
    public void Selecting_the_server_row_shows_the_servers_own_log()
    {
        var vm = MakeWithRoster("fp-1");
        _remote.ServerLog = FakeRemoteLogSource.Lines("server line");

        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Kind == LogSidebarItemKind.Server);
        PumpUntil(() => vm.DisplayLines.Count == 1 && vm.DisplayLines[0].Contains("server line"));

        Assert.Contains("server line", Assert.Single(vm.DisplayLines));
    }

    // A fetch for a row the user has already navigated away from must not paint
    // its lines under the new selection - the failure this guards against is
    // one device's log appearing under another device's name.
    [AvaloniaFact]
    public void A_fetch_that_lands_after_the_selection_moved_is_discarded()
    {
        var vm = MakeWithRoster("fp-1", "fp-2");
        _remote.DeviceLogs["fp-1"] = FakeRemoteLogSource.Lines("belongs to fp-1");
        _remote.DeviceLogs["fp-2"] = FakeRemoteLogSource.Lines("belongs to fp-2");

        // Hold fp-1's fetch open, move to fp-2, then let both complete.
        var gate = new TaskCompletionSource();
        _remote.Gate = gate;
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-2");
        _remote.Gate = null;
        gate.SetResult();

        PumpUntil(() => vm.DisplayLines.Count == 1 && vm.DisplayLines[0].Contains("belongs to fp-2"));
        Assert.DoesNotContain(vm.DisplayLines, l => l.Contains("belongs to fp-1"));
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

    // Switching selection resets the comparison, so the newly loaded content
    // counts as growth rather than being measured against the last device.
    // The count comparison only makes sense within one selection. Switching
    // from a long local log to a client's one-line snapshot must still report
    // growth - measured against the previous device it would read as a
    // shrink, and the newly loaded pane would never scroll into view.
    [AvaloniaFact]
    public void Switching_selection_reports_growth_for_the_newly_loaded_content()
    {
        for (var i = 0; i < 5; i++)
            LogLocal($"local line {i}");
        var vm = MakeWithRoster("fp-1");
        _remote.DeviceLogs["fp-1"] = FakeRemoteLogSource.Lines("one");
        Assert.True(vm.DisplayLines.Count > 1, "This Device should have more lines than the pushed snapshot");

        bool? grew = null;
        vm.LinesReset += (_, g) => grew = g;
        Select(vm, "fp-1");

        Assert.True(grew);
    }

    // ── Live local entries ────────────────────────────────────────────────────

    [AvaloniaFact]
    public void A_new_local_entry_is_appended_rather_than_re_rendering_the_document()
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
    // FlushPendingLocalEntries, not the _flushScheduled flag - that flag only
    // suppresses redundant dispatcher posts, which have no effect on the events
    // this class emits and so cannot be observed from out here.
    [AvaloniaFact]
    public void A_burst_of_local_entries_is_coalesced_into_fewer_batches_than_lines()
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
    public void A_filtered_out_local_entry_appends_nothing()
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

    // Local entries are this device's live log, so they must not leak into a
    // client's pane while it is selected. Two guards enforce that - one in
    // OnLocalEntryAdded and one in FlushPendingLocalEntries - and only the
    // second is load-bearing: it also covers the selection moving away while a
    // flush is already in flight, which is the case that actually happens.
    [AvaloniaFact]
    public void Local_entries_are_ignored_while_a_client_is_selected()
    {
        var vm = MakeWithRoster("fp-1");
        _remote.DeviceLogs["fp-1"] = FakeRemoteLogSource.Lines("client line");
        Select(vm, "fp-1");

        var appended = 0;
        vm.LinesAppended += (_, _) => appended++;

        LogLocal("local line while a client is shown");
        Drain(200);

        Assert.Equal(0, appended);
        Assert.Equal(new[] { "client line" }, vm.DisplayLines.Select(l => l[^"client line".Length..]).ToArray());
    }
}
