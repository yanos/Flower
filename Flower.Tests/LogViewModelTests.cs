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
// vs. a paired client's pushed snapshot" selector behind the Log window, and
// its whole contract with the View is two events rather than a bindable
// collection: LinesReset (replace the document) and LinesAppended (append one
// coalesced batch). Both are asserted here directly.
//
// InMemoryLogStore is a process-wide singleton with a private constructor, so
// there is no fresh instance to hand this ViewModel - every test therefore
// tags its entries with a unique marker and sets FilterText to it, which
// isolates DisplayLines from whatever the rest of the suite is logging in
// parallel. [AvaloniaFact] because the local-entry flush and the client
// snapshot refresh both go through Dispatcher.UIThread.Post.
[Collection("PlatformDataDirectory")]
public class LogViewModelTests : PinnedDataDirectory
{
    private readonly ClientLogStore _clientLogStore = new();
    private readonly TrustedPeerStore _trustedPeerStore = new(NullLogger<TrustedPeerStore>.Instance);
    private readonly DeviceNicknameStore _nicknameStore = new(NullLogger<DeviceNicknameStore>.Instance);
    private readonly string _marker = "marker-" + Guid.NewGuid().ToString("N");

    private LogViewModel Make(AppSettings? settings = null) =>
        new(InMemoryLogStore.Instance, _clientLogStore, settings ?? new AppSettings(),
            new AppSettingsStore(NullLogger<AppSettingsStore>.Instance),
            _trustedPeerStore, _nicknameStore);

    // Logs one entry to the shared local store, tagged so this test can find it.
    private void LogLocal(string message, string level = "Information", string? source = null) =>
        InMemoryLogStore.Instance.Add(
            new InMemoryLogEntry(DateTimeOffset.Now, level, source, $"{_marker} {message}", null));

    private static List<LogEntryDto> Dtos(params string[] messages) =>
        messages.Select(m => new LogEntryDto(DateTimeOffset.UtcNow, "Information", null, m, null)).ToList();

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

    // Client rows only exist when this instance is running as a Server - a
    // Client never has anyone pushing logs to it.
    [Fact]
    public void Paired_client_rows_only_appear_when_running_as_a_server()
    {
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();

        Assert.Single(Make(new AppSettings { IsServer = false }).SidebarItems);
        Assert.Equal(2, Make(new AppSettings { IsServer = true }).SidebarItems.Count);
    }

    // RefreshSidebarItems runs again every time the window is reopened, since
    // trusting a peer has no live notification of its own.
    [Fact]
    public void Refreshing_the_sidebar_keeps_the_current_selection_when_it_still_exists()
    {
        var settings = new AppSettings { IsServer = true };
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();
        var vm = Make(settings);
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");

        _trustedPeerStore.ApproveAsync("fp-2", "Another Phone", "key").GetAwaiter().GetResult();
        vm.RefreshSidebarItems();

        Assert.Equal(3, vm.SidebarItems.Count);
        Assert.Equal("fp-1", vm.SelectedSidebarItem!.Fingerprint);
    }

    // A revoked peer's row is gone, so the selection has to fall back rather
    // than dangle.
    [Fact]
    public void Refreshing_the_sidebar_falls_back_to_This_Device_when_the_selection_vanished()
    {
        var settings = new AppSettings { IsServer = true };
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();
        var vm = Make(settings);
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");

        _trustedPeerStore.RevokeAsync("fp-1").GetAwaiter().GetResult();
        vm.RefreshSidebarItems();

        Assert.Equal(LogSidebarItemKind.ThisDevice, vm.SelectedSidebarItem!.Kind);
    }

    // A client that has never pushed anything gets an explicit placeholder,
    // not a blank pane indistinguishable from "connected but silent".
    [Fact]
    public void A_client_with_no_snapshot_yet_shows_a_placeholder_line()
    {
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();
        var vm = Make(new AppSettings { IsServer = true });

        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");

        Assert.Equal(new[] { "(no log snapshot received from this device yet)" }, vm.DisplayLines.ToArray());
    }

    [Fact]
    public void Selecting_a_client_shows_its_pushed_snapshot()
    {
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();
        _clientLogStore.SetSnapshot("fp-1", "A Phone", Dtos("client line one", "client line two"), DateTimeOffset.UtcNow);
        var vm = Make(new AppSettings { IsServer = true });

        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");

        Assert.Equal(2, vm.DisplayLines.Count);
        Assert.Contains("client line one", vm.DisplayLines[0]);
        Assert.Contains("client line two", vm.DisplayLines[1]);
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
    [Fact]
    public void Switching_selection_reports_growth_for_the_newly_loaded_content()
    {
        for (var i = 0; i < 5; i++)
            LogLocal($"local line {i}");
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();
        _clientLogStore.SetSnapshot("fp-1", "A Phone", Dtos("one"), DateTimeOffset.UtcNow);

        var vm = Make(new AppSettings { IsServer = true });
        Assert.True(vm.DisplayLines.Count > 1, "This Device should have more lines than the client snapshot");

        bool? grew = null;
        vm.LinesReset += (_, g) => grew = g;
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");

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
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();
        _clientLogStore.SetSnapshot("fp-1", "A Phone", Dtos("client line"), DateTimeOffset.UtcNow);
        var vm = Make(new AppSettings { IsServer = true });
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");

        var appended = 0;
        vm.LinesAppended += (_, _) => appended++;

        LogLocal("local line while a client is shown");
        Drain(200);

        Assert.Equal(0, appended);
        Assert.Equal(new[] { "client line" }, vm.DisplayLines.Select(l => l[^"client line".Length..]).ToArray());
    }

    // ── Client snapshot refresh ───────────────────────────────────────────────

    // Each push is a fresh full snapshot, so the pane replaces rather than
    // appends - a client that restarted must not show its old lines twice.
    [AvaloniaFact]
    public void A_new_snapshot_for_the_selected_client_replaces_the_pane()
    {
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();
        _clientLogStore.SetSnapshot("fp-1", "A Phone", Dtos("old one", "old two"), DateTimeOffset.UtcNow);
        var vm = Make(new AppSettings { IsServer = true });
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");

        _clientLogStore.SetSnapshot("fp-1", "A Phone", Dtos("new one"), DateTimeOffset.UtcNow);
        PumpUntil(() => vm.DisplayLines.Count == 1);

        Assert.Contains("new one", vm.DisplayLines[0]);
    }

    // A push from some other client must not disturb the pane currently shown.
    [AvaloniaFact]
    public void A_snapshot_for_a_different_client_leaves_the_pane_alone()
    {
        _trustedPeerStore.ApproveAsync("fp-1", "A Phone", "key").GetAwaiter().GetResult();
        _trustedPeerStore.ApproveAsync("fp-2", "Another Phone", "key").GetAwaiter().GetResult();
        _clientLogStore.SetSnapshot("fp-1", "A Phone", Dtos("mine"), DateTimeOffset.UtcNow);
        var vm = Make(new AppSettings { IsServer = true });
        vm.SelectedSidebarItem = vm.SidebarItems.First(i => i.Fingerprint == "fp-1");

        var resets = 0;
        vm.LinesReset += (_, _) => resets++;
        _clientLogStore.SetSnapshot("fp-2", "Another Phone", Dtos("theirs"), DateTimeOffset.UtcNow);
        Drain(200);

        Assert.Equal(0, resets);
        Assert.Contains("mine", Assert.Single(vm.DisplayLines));
    }
}
