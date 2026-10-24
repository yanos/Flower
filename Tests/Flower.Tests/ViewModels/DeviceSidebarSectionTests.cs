using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Services;
using Flower.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

// One of the collaborators split out of MainViewModel (docs/ARCHITECTURE-
// REVIEW.md Tier 4.2), constructed directly rather than through that class's
// constructor and the whole service graph behind it.
//
// The device-row state machine, driven without a MainViewModel at all - the
// pre-split version of these had to go through the full ViewModel.
public class DeviceSidebarSectionTests
{
    private sealed class Host : IDeviceSidebarHost
    {
        public string? PairedServerFingerprint { get; set; }
        public bool IsSyncing { get; set; }
        public SidebarItem? SelectedSidebarItem { get; set; }
        public SidebarItem? DefaultSelection { get; set; }
        public readonly List<string> Forgotten = new();
        public int RowsChangedCount;
        public void ForgetSyncedDevice(string fingerprint) => Forgotten.Add(fingerprint);
        public void DeviceRowsChanged() => RowsChangedCount++;
    }

    private static int _port = 7000;

    private static DiscoveredDevice Device(string instanceName, string fingerprint = "", string? alias = null) =>
        new()
        {
            InstanceName = instanceName,
            Fingerprint = fingerprint,
            Alias = alias ?? instanceName,
            BaseUri = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Loopback, System.Threading.Interlocked.Increment(ref _port))),
        };

    private static (DeviceSidebarSection Section, ObservableCollection<SidebarItem> Items, Host Host) Make()
    {
        var items = new ObservableCollection<SidebarItem>
        {
            new(SidebarItemKind.Header, "Library"),
            new(SidebarItemKind.Songs, "Songs"),
        };
        var host = new Host { DefaultSelection = items[1] };
        return (new DeviceSidebarSection(items, host, nicknames: null, reachability: null), items, host);
    }

    // A server's row shows its own icon: the paired server's as this device
    // keeps it, and any other server's emoji as its /info gave it.
    [AvaloniaFact]
    public void A_server_row_shows_that_servers_icon()
    {
        var items = new ObservableCollection<SidebarItem> { new(SidebarItemKind.Songs, "Songs") };
        var host = new Host { DefaultSelection = items[0], PairedServerFingerprint = "paired-fp" };
        var settings = new Flower.Persistence.AppSettings { PairedServerFingerprint = "paired-fp", PairedServerIconEmoji = "🏠" };
        var pairedIcon = new PairedServerIcon(settings, null, new TestSupport.StaticPeerCredentials("X-Test", "1"),
            new System.Net.Http.HttpClient(), NullLogger<PairedServerIcon>.Instance,
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"flower-icon-{Guid.NewGuid():N}"));
        var section = new DeviceSidebarSection(items, host, nicknames: null, reachability: null, pairedIcon: pairedIcon);

        var other = Device("attic.local", "other-fp", "Attic");
        other.IconEmoji = "🎧";
        other.IconToken = "not-ours-to-fetch";
        section.AddOrUpdate(Device("home.local", "paired-fp", "Home"));
        section.AddOrUpdate(other);

        Assert.Equal("🏠", items.Single(i => i.Name == "Home").IconEmoji);
        var attic = items.Single(i => i.Name == "Attic");
        Assert.Equal("🎧", attic.IconEmoji);
        Assert.Null(attic.IconImage);
    }

    [Fact]
    public void A_peer_with_no_resolved_fingerprint_yet_gets_no_row()
    {
        var (section, items, _) = Make();

        // Showing it now would display the raw mDNS instance name; the
        // discovery re-fires once /info answers with a real alias.
        section.AddOrUpdate(Device("laptop.local"));

        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Device);
    }

    // One section, because there is one kind of thing to find. This used to
    // fork on the peer's advertised role, back when a Flower app could be
    // flipped into being a server too.
    [Fact]
    public void Every_discovered_device_lands_under_one_Servers_section()
    {
        var (section, items, _) = Make();

        section.AddOrUpdate(Device("laptop", "fp-laptop", "Laptop"));
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Devices");
        var header = Assert.Single(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Servers");

        // The section's members sit contiguously right after its own header,
        // which is what lets a row's section be found by walking backward.
        var headerIndex = items.IndexOf(header);
        Assert.Equal("Laptop", items[headerIndex + 1].Name);
        Assert.Equal("NAS", items[headerIndex + 2].Name);
    }

    // A second sighting of a peer already listed updates the row in place
    // rather than adding another, and does not disturb the selection sitting
    // on it.
    [Fact]
    public void Re_announcing_a_listed_peer_keeps_its_row_and_its_selection()
    {
        var (section, items, host) = Make();
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));
        var row = items.Single(i => i.Kind == SidebarItemKind.Device);
        host.SelectedSidebarItem = row;

        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        Assert.Same(row, items.Single(i => i.Kind == SidebarItemKind.Device));
        Assert.Same(row, host.SelectedSidebarItem);
    }

    [Fact]
    public void Two_rows_for_one_peer_collapse_once_a_fingerprint_resolves()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = null;

        // Bonjour's collision-avoidance can republish the same physical device
        // under an auto-renamed instance name before the old advertisement is
        // withdrawn.
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));
        section.AddOrUpdate(Device("nas-2", "fp-nas", "NAS"));

        var row = Assert.Single(items, i => i.Kind == SidebarItemKind.Device);
        Assert.Equal("nas-2", row.Device!.InstanceName);
        // The surviving row is the same still-present device, so its once-per-
        // session sync dedup must NOT be cleared.
        Assert.Empty(host.Forgotten);
    }

    [Fact]
    public void Two_distinct_devices_sharing_a_computer_name_stay_separate()
    {
        var (section, items, _) = Make();

        section.AddOrUpdate(Device("macbook", "fp-one", "MacBook"));
        section.AddOrUpdate(Device("macbook", "fp-two", "MacBook"));

        // Matching on InstanceName regardless of a resolved fingerprint would
        // conflate them and pin one row at the wrong endpoint.
        Assert.Equal(2, items.Count(i => i.Kind == SidebarItemKind.Device));
        // Colliding display names each get their IP as a subtitle to tell them
        // apart - sync and trust key off Fingerprint, so this is purely visual.
        Assert.All(items.Where(i => i.Kind == SidebarItemKind.Device), i => Assert.NotNull(i.Subtitle));
    }

    [Fact]
    public void A_goodbye_for_an_ambiguous_instance_name_removes_nothing()
    {
        var (section, items, host) = Make();
        section.AddOrUpdate(Device("macbook", "fp-one", "MacBook"));
        section.AddOrUpdate(Device("macbook", "fp-two", "MacBook"));

        // mDNS's goodbye carries only the instance name, so there is no way to
        // tell which of the two actually left - guessing wrong is worse.
        section.Remove("macbook");

        Assert.Equal(2, items.Count(i => i.Kind == SidebarItemKind.Device));
        Assert.Empty(host.Forgotten);
    }

    [Fact]
    public void A_departing_peer_is_removed_reselected_away_from_and_forgotten()
    {
        var (section, items, host) = Make();
        section.AddOrUpdate(Device("laptop", "fp-laptop", "Laptop"));
        host.SelectedSidebarItem = items.Single(i => i.Kind == SidebarItemKind.Device);

        section.Remove("laptop");

        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Device);
        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Devices");
        Assert.Same(host.DefaultSelection, host.SelectedSidebarItem);
        // Cleared so a fresh sync fires if it comes back later this session.
        Assert.Equal(new[] { "fp-laptop" }, host.Forgotten);
    }

    [Fact]
    public void The_paired_server_row_survives_going_offline()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));
        Assert.True(items.Single(i => i.Kind == SidebarItemKind.Device).IsPairedServer);

        section.Remove("nas");

        // Pinned in place rather than disappearing - it just goes unreachable.
        var row = Assert.Single(items, i => i.Kind == SidebarItemKind.Device);
        Assert.True(row.IsPairedServer);
        Assert.False(row.IsReachable);
    }

    [Fact]
    public void Unpinning_removes_a_row_that_has_no_live_device_behind_it()
    {
        var (section, items, host) = Make();
        // What MainViewModel's BuildSidebarItems seeds at launch: the pairing is
        // known from settings, but this session has discovered nothing yet.
        items.Add(new SidebarItem(SidebarItemKind.Header, "Servers"));
        items.Add(new SidebarItem(SidebarItemKind.Device, "NAS") { IsPairedServer = true });

        section.UnpinPairedServerRow();

        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Device);
        Assert.DoesNotContain(items, i => i.Kind == SidebarItemKind.Header && i.Name == "Servers");
    }

    [Fact]
    public void Unpinning_keeps_a_row_that_is_still_discovered()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        host.PairedServerFingerprint = null;
        section.UnpinPairedServerRow();

        var row = Assert.Single(items, i => i.Kind == SidebarItemKind.Device);
        Assert.False(row.IsPairedServer);
    }

    [Fact]
    public void A_rediscovered_peer_claims_the_pinned_placeholder_instead_of_adding_a_second_row()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        items.Add(new SidebarItem(SidebarItemKind.Header, "Servers"));
        items.Add(new SidebarItem(SidebarItemKind.Device, "NAS") { IsPairedServer = true });

        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        var row = Assert.Single(items, i => i.Kind == SidebarItemKind.Device);
        Assert.NotNull(row.Device);
        Assert.True(row.IsPairedServer);
    }

    [Fact]
    public void A_row_updated_mid_sync_keeps_its_spinner()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        host.IsSyncing = true;

        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));

        // The spinner is only pushed on IsSyncing's own edges, so a row created
        // or re-created mid-sync has to carry the current state forward.
        Assert.True(items.Single(i => i.Kind == SidebarItemKind.Device).IsSyncing);
    }

    // The pinned row exists before the peer has been discovered, and for a
    // server reached over a tailnet it may never be discovered at all - so it
    // has no Device to match a fingerprint against. This used to look the row
    // up by Device?.Fingerprint and therefore find nothing, leaving the
    // sidebar showing its green check for the whole of a sync while the device
    // pane and the settings picker, which read MainViewModel directly, spun
    // beside it: one server, two answers, on screen at once.
    [Fact]
    public void The_pinned_row_spins_during_a_sync_even_before_its_peer_is_discovered()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        items.Add(new SidebarItem(SidebarItemKind.Header, "Servers"));
        items.Add(new SidebarItem(SidebarItemKind.Device, "NAS") { IsPairedServer = true });

        section.SetPairedServerSyncing(true);

        Assert.True(items.Single(i => i.Kind == SidebarItemKind.Device).IsSyncing);

        section.SetPairedServerSyncing(false);

        Assert.False(items.Single(i => i.Kind == SidebarItemKind.Device).IsSyncing);
    }

    // Only the paired server's row, not every server the network happens to
    // be advertising.
    [Fact]
    public void A_sync_spins_only_the_paired_servers_row()
    {
        var (section, items, host) = Make();
        host.PairedServerFingerprint = "fp-nas";
        section.AddOrUpdate(Device("nas", "fp-nas", "NAS"));
        section.AddOrUpdate(Device("other", "fp-other", "Other"));
        section.PinPairedServerRow("fp-nas");

        section.SetPairedServerSyncing(true);

        Assert.True(items.Single(i => i.Name == "NAS").IsSyncing);
        Assert.False(items.Single(i => i.Name == "Other").IsSyncing);
    }
}
