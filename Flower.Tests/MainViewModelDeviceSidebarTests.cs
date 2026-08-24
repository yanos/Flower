using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

using Avalonia.Headless.XUnit;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Material.Icons;

using Xunit;

namespace Flower.Tests;

// docs/ARCHITECTURE-REVIEW.md §5.6: MainViewModel's sync/pairing/device-row
// state machine - the sidebar's Devices/Server sections, the pinned
// paired-server row, identity matching across an unresolved fingerprint, and
// the pair/unpair transitions.
//
// The device-row entry points (AddOrUpdateDeviceSidebarItem /
// RemoveDeviceSidebarItem) are driven directly rather than through the real
// NetworkDiscoveryService. Going through it would mean standing up an mDNS
// backend *and* an HTTP /info endpoint for every case just to choose a
// device's Fingerprint - the resolution handshake is the only thing that ever
// sets one. NetworkDiscoveryServiceTests already covers that handshake and
// the discovery events it emits; what is untested, and what these cover, is
// what MainViewModel does with the resulting DiscoveredDevice.
[Collection("PlatformDataDirectory")]
public class MainViewModelDeviceSidebarTests : PinnedDataDirectory
{
    private static int _port = 5000;

    private static DiscoveredDevice Device(
        string instanceName, string fingerprint = "", string? alias = null, bool isServer = false) =>
        new()
        {
            InstanceName = instanceName,
            BaseUri     = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Parse("192.168.1." + (++_port % 250 + 2)), 53317)),
            Alias        = alias ?? instanceName,
            Fingerprint  = fingerprint,
            IsServer     = isServer,
        };

    private static DiscoveredDevice DeviceAt(string instanceName, string ip, string fingerprint = "", string? alias = null) =>
        new()
        {
            InstanceName = instanceName,
            BaseUri     = NetworkDiscoveryService.HttpOrigin(new IPEndPoint(IPAddress.Parse(ip), 53317)),
            Alias        = alias ?? instanceName,
            Fingerprint  = fingerprint,
        };

    // Owned by the fixture (not static any more) so the log-push timer inside
    // every one of these MainViewModels is stopped at teardown - see
    // MainViewModelHarness.Parts.
    private MainViewModel Make(AppSettings? settings = null) =>
        Own(MainViewModelHarness.Build(new Library(new List<Track>()), new MainPlaylist(new List<Track>()), settings)).Main;

    private static List<SidebarItem> DeviceRows(MainViewModel vm) =>
        vm.SidebarItems.Where(i => i.Kind == SidebarItemKind.Device).ToList();

    // The section header a row currently sits under - the sidebar keeps each
    // section's members contiguous right after its own Header.
    private static string? SectionOf(MainViewModel vm, SidebarItem item)
    {
        var index = vm.SidebarItems.IndexOf(item);
        for (var i = index - 1; i >= 0; i--)
        {
            if (vm.SidebarItems[i].Kind == SidebarItemKind.Header)
                return vm.SidebarItems[i].Name;
        }
        return null;
    }

    private static SidebarItem SingleDeviceRow(MainViewModel vm) => Assert.Single(DeviceRows(vm));

    // ── Sections ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void An_ordinary_peer_lands_under_Devices_and_a_server_under_Server()
    {
        var vm = Make();

        vm.AddOrUpdateDeviceSidebarItem(Device("phone", "fp-phone", "Phone"));
        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop", isServer: true));

        var phone = DeviceRows(vm).Single(i => i.Name == "Phone");
        var desk  = DeviceRows(vm).Single(i => i.Name == "Desktop");
        Assert.Equal("Devices", SectionOf(vm, phone));
        Assert.Equal("Server", SectionOf(vm, desk));
        Assert.Equal(MaterialIconKind.Laptop, phone.Icon);
        Assert.Equal(MaterialIconKind.Server, desk.Icon);
    }

    // A peer flipping its own "Act as Server" setting moves its row between
    // sections in place, keeping the same SidebarItem instance.
    [AvaloniaFact]
    public void A_peer_that_starts_advertising_server_mode_moves_sections()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop"));
        var row = SingleDeviceRow(vm);
        Assert.Equal("Devices", SectionOf(vm, row));

        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop", isServer: true));

        Assert.Same(row, SingleDeviceRow(vm));
        Assert.Equal("Server", SectionOf(vm, row));
        Assert.Equal(MaterialIconKind.Server, row.Icon);
    }

    // The vacated section's header goes with its last member rather than
    // lingering as an empty heading.
    [AvaloniaFact]
    public void A_section_header_disappears_once_its_last_member_leaves()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop"));
        Assert.Contains(vm.SidebarItems, i => i.Kind == SidebarItemKind.Header && i.Name == "Devices");

        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop", isServer: true));

        Assert.DoesNotContain(vm.SidebarItems, i => i.Kind == SidebarItemKind.Header && i.Name == "Devices");
        Assert.Contains(vm.SidebarItems, i => i.Kind == SidebarItemKind.Header && i.Name == "Server");
    }

    // Moving a row removes and reinserts it, which drops it out of the
    // sidebar's two-way selection binding - it has to be put back.
    [AvaloniaFact]
    public void Relocating_the_selected_row_keeps_it_selected()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop"));
        var row = SingleDeviceRow(vm);
        vm.SelectedSidebarItem = row;

        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop", isServer: true));

        Assert.Same(row, vm.SelectedSidebarItem);
    }

    // ── Identity matching ─────────────────────────────────────────────────────

    // Fingerprint is the peer's stable per-install identity, so the same one
    // arriving under a different mDNS instance name is still one device.
    [AvaloniaFact]
    public void The_same_fingerprint_under_a_new_instance_name_updates_one_row()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("phone", "fp-phone", "Phone"));

        vm.AddOrUpdateDeviceSidebarItem(Device("phone-2", "fp-phone", "Phone Renamed"));

        var row = SingleDeviceRow(vm);
        Assert.Equal("Phone Renamed", row.Name);
        Assert.Equal("phone-2", row.Device!.InstanceName);
    }

    // Two genuinely distinct devices can share an unrenamed default computer
    // name. Conflating them would pin one row's Device at the wrong endpoint.
    [AvaloniaFact]
    public void Two_devices_sharing_an_instance_name_but_not_a_fingerprint_stay_separate()
    {
        var vm = Make();

        vm.AddOrUpdateDeviceSidebarItem(Device("macbook", "fp-a", "MacBook A"));
        vm.AddOrUpdateDeviceSidebarItem(Device("macbook", "fp-b", "MacBook B"));

        Assert.Equal(2, DeviceRows(vm).Count);
    }

    // An instance-name match is only trusted against another still-unresolved
    // row: a row that already has a different, resolved fingerprint is a
    // distinct device that merely shares an unrenamed computer name, so the
    // unresolved arrival must not claim it. Having matched nothing, it is then
    // held back entirely rather than shown under its raw mDNS name.
    [AvaloniaFact]
    public void An_unresolved_device_does_not_claim_an_already_resolved_row()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("laptop", "fp-resolved", "Resolved Laptop"));

        vm.AddOrUpdateDeviceSidebarItem(Device("laptop"));

        var row = Assert.Single(DeviceRows(vm));
        Assert.Equal("fp-resolved", row.Device!.Fingerprint);
        Assert.Equal("Resolved Laptop", row.Name);
    }

    // A peer with no fingerprint yet must not appear under its raw mDNS
    // instance name; it reappears with its real alias once /info answers.
    [AvaloniaFact]
    public void A_brand_new_device_with_no_fingerprint_yet_creates_no_row()
    {
        var vm = Make();

        vm.AddOrUpdateDeviceSidebarItem(Device("localhost-iOS"));

        Assert.Empty(DeviceRows(vm));
    }

    // Once a fingerprint resolves and turns out to match a row already tracked
    // under a different instance name, the extras are revealed as the same
    // physical device and collapse to one row.
    // A peer can transiently be advertised under two instance names at once
    // (a prior run's record not cleanly withdrawn before a fresh one
    // republished under an auto-renamed name). ResolveAliasAsync mutates the
    // DiscoveredDevice in place, so when the second one's /info resolves to a
    // fingerprint already tracked, the two rows are revealed as one device.
    [AvaloniaFact]
    public void Resolving_a_fingerprint_collapses_duplicate_rows_for_one_device()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("phone", "fp-phone", "Phone"));
        var stale = Device("phone-2", "fp-stale", "Phone (2)");
        vm.AddOrUpdateDeviceSidebarItem(stale);
        Assert.Equal(2, DeviceRows(vm).Count);

        // The handshake resolves it to the same install as "phone", in place.
        stale.Fingerprint = "fp-phone";
        vm.AddOrUpdateDeviceSidebarItem(stale);

        var row = SingleDeviceRow(vm);
        Assert.Equal("fp-phone", row.Device!.Fingerprint);
    }

    // ── Display names ─────────────────────────────────────────────────────────

    // Two devices legitimately sharing a display name is cosmetic only (trust
    // and sync key off Fingerprint), but the user still needs to tell them
    // apart - so, and only then, each shows its IP.
    [AvaloniaFact]
    public void Colliding_display_names_get_an_IP_subtitle_and_lose_it_again()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(DeviceAt("a", "192.168.1.10", "fp-a", "MacBook"));
        vm.AddOrUpdateDeviceSidebarItem(DeviceAt("b", "192.168.1.11", "fp-b", "MacBook"));

        Assert.All(DeviceRows(vm), r => Assert.NotNull(r.Subtitle));
        Assert.Equal(new[] { "192.168.1.10", "192.168.1.11" },
                     DeviceRows(vm).Select(r => r.Subtitle).OrderBy(x => x).ToArray());

        // One of them is renamed by its owner - no collision left.
        vm.AddOrUpdateDeviceSidebarItem(DeviceAt("b", "192.168.1.11", "fp-b", "Other MacBook"));

        Assert.All(DeviceRows(vm), r => Assert.Null(r.Subtitle));
    }

    [AvaloniaFact]
    public void A_unique_display_name_carries_no_subtitle()
    {
        var vm = Make();

        vm.AddOrUpdateDeviceSidebarItem(Device("phone", "fp-phone", "Phone"));

        Assert.Null(SingleDeviceRow(vm).Subtitle);
    }

    // ── Pairing ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Pairing_pins_the_devices_row_as_the_paired_server()
    {
        var vm = Make();
        var device = Device("desk", "fp-desk", "Desktop", isServer: true);
        vm.AddOrUpdateDeviceSidebarItem(device);

        vm.PairWithServer(device);

        var row = SingleDeviceRow(vm);
        Assert.True(row.IsPairedServer);
        Assert.Equal("fp-desk", vm.PairedServerFingerprint);
        Assert.Equal("Desktop", vm.PairedServerAlias);
    }

    [AvaloniaFact]
    public void Unpairing_clears_the_pointer_and_unpins_the_row()
    {
        var vm = Make();
        var device = Device("desk", "fp-desk", "Desktop", isServer: true);
        vm.AddOrUpdateDeviceSidebarItem(device);
        vm.PairWithServer(device);

        vm.UnpairServer();

        Assert.Null(vm.PairedServerFingerprint);
        Assert.Null(vm.PairedServerAlias);
        // Still discovered, so the row stays - it just drops back to being an
        // ordinary Server-section row.
        Assert.False(SingleDeviceRow(vm).IsPairedServer);
    }

    // Becoming a Server yourself makes any pairing you held as a Client stale -
    // the pointer is cleared and its row unpinned, deliberately without
    // syncing with the old server again.
    [AvaloniaFact]
    public void Becoming_a_server_clears_the_pairing_and_unpins_its_row()
    {
        var vm = Make();
        var device = Device("desk", "fp-desk", "Desktop", isServer: true);
        vm.AddOrUpdateDeviceSidebarItem(device);
        vm.PairWithServer(device);

        vm.IsServer = true;

        Assert.Null(vm.PairedServerFingerprint);
        Assert.Null(vm.PairedServerAlias);
        Assert.False(SingleDeviceRow(vm).IsPairedServer);
    }

    // The same flip while holding a pairing whose server was never discovered:
    // that row is a placeholder with no live Device, so unpinning leaves it
    // with nothing to show and it goes entirely.
    [AvaloniaFact]
    public void Becoming_a_server_removes_an_undiscovered_paired_server_row_outright()
    {
        var vm = Make(new AppSettings
        {
            PairedServerFingerprint = "fp-desk",
            PairedServerAlias       = "Desktop",
        });
        Assert.Single(DeviceRows(vm));

        vm.IsServer = true;

        Assert.Empty(DeviceRows(vm));
        Assert.DoesNotContain(vm.SidebarItems, i => i.Kind == SidebarItemKind.Header && i.Name == "Server");
    }

    // The paired server's row is pinned for the whole session rather than
    // disappearing the moment mDNS loses sight of it - it flips to
    // unreachable instead, so the user can see it is paired but offline.
    [AvaloniaFact]
    public void A_paired_server_going_offline_keeps_its_row_and_marks_it_unreachable()
    {
        var vm = Make();
        var device = Device("desk", "fp-desk", "Desktop", isServer: true);
        vm.AddOrUpdateDeviceSidebarItem(device);
        vm.PairWithServer(device);

        vm.RemoveDeviceSidebarItem("desk");

        var row = SingleDeviceRow(vm);
        Assert.True(row.IsPairedServer);
        Assert.False(row.IsReachable);
        Assert.True(row.ShowUnreachableIcon);
    }

    // An ordinary, unpaired peer does disappear when it goes offline.
    [AvaloniaFact]
    public void An_unpaired_device_going_offline_removes_its_row_and_section()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("phone", "fp-phone", "Phone"));

        vm.RemoveDeviceSidebarItem("phone");

        Assert.Empty(DeviceRows(vm));
        Assert.DoesNotContain(vm.SidebarItems, i => i.Kind == SidebarItemKind.Header && i.Name == "Devices");
    }

    // Removing the row the user is looking at has to move the selection
    // somewhere real rather than leaving it pointing at a detached item.
    [AvaloniaFact]
    public void Removing_the_selected_device_row_falls_back_to_Songs()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("phone", "fp-phone", "Phone"));
        vm.SelectedSidebarItem = SingleDeviceRow(vm);

        vm.RemoveDeviceSidebarItem("phone");

        Assert.Equal(SidebarItemKind.Songs, vm.SelectedSidebarItem!.Kind);
    }

    // mDNS's goodbye carries only an instance name, so when two distinct
    // devices are colliding on one there is no way to tell which actually
    // left - it deliberately does nothing rather than drop the wrong row.
    [AvaloniaFact]
    public void An_ambiguous_goodbye_removes_nothing()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("macbook", "fp-a", "MacBook A"));
        vm.AddOrUpdateDeviceSidebarItem(Device("macbook", "fp-b", "MacBook B"));

        vm.RemoveDeviceSidebarItem("macbook");

        Assert.Equal(2, DeviceRows(vm).Count);
    }

    // A paired server that was never discovered this session still gets a row
    // up front, so the user sees the pairing at launch rather than nothing.
    [AvaloniaFact]
    public void A_paired_server_absent_at_launch_still_gets_a_pinned_placeholder_row()
    {
        var vm = Make(new AppSettings
        {
            PairedServerFingerprint = "fp-desk",
            PairedServerAlias       = "Desktop",
        });

        var row = Assert.Single(DeviceRows(vm));
        Assert.True(row.IsPairedServer);
        Assert.False(row.IsReachable);
        Assert.Equal("Desktop", row.Name);
        Assert.Null(row.Device); // nothing discovered yet this session
        Assert.Equal("Server", SectionOf(vm, row));
    }

    // The placeholder is claimed by the real device when it turns up, rather
    // than a second row appearing for the same peer.
    // The launch placeholder has no Device to match on, so it is claimed by
    // fingerprint against PairedServerFingerprint - otherwise a second row
    // would appear for the same peer the moment it is discovered.
    [AvaloniaFact]
    public void The_launch_placeholder_is_claimed_rather_than_duplicated_on_discovery()
    {
        var vm = Make(new AppSettings
        {
            PairedServerFingerprint = "fp-desk",
            PairedServerAlias       = "Desktop",
        });
        var placeholder = Assert.Single(DeviceRows(vm));

        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop", isServer: true));

        var row = Assert.Single(DeviceRows(vm));
        Assert.Same(placeholder, row);
        Assert.NotNull(row.Device);
        Assert.True(row.IsPairedServer);
    }

    // A different peer must not be mistaken for the pinned placeholder just
    // because the placeholder has no Device to compare against.
    [AvaloniaFact]
    public void An_unrelated_peer_does_not_claim_the_launch_placeholder()
    {
        var vm = Make(new AppSettings
        {
            PairedServerFingerprint = "fp-desk",
            PairedServerAlias       = "Desktop",
        });

        vm.AddOrUpdateDeviceSidebarItem(Device("phone", "fp-phone", "Phone"));

        Assert.Equal(2, DeviceRows(vm).Count);
        Assert.Null(DeviceRows(vm).Single(r => r.IsPairedServer).Device);
    }

    // ── AvailableServers ──────────────────────────────────────────────────────

    // The pool ServerPickerView picks from is every discovered peer advertising
    // Server mode - unrelated to trust.
    [AvaloniaFact]
    public void Only_devices_advertising_server_mode_are_pairing_candidates()
    {
        var vm = Make();
        vm.AddOrUpdateDeviceSidebarItem(Device("phone", "fp-phone", "Phone"));
        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop", isServer: true));

        // Sourced from NetworkDiscoveryService.KnownDevices, which nothing has
        // populated here - the point being it is not derived from the sidebar.
        Assert.DoesNotContain(vm.AvailableServers, d => d.Fingerprint == "fp-phone");
    }

    // ── Handing out pairing codes ─────────────────────────────────────────────

    // A headless Flower.Server (DeviceType "server", so PairsByCode) that has
    // already approved this device: the device-detail header offers to issue a
    // pairing code for somebody else's device against it. Deliberately not also
    // gated on this device being an administrator - nothing here can know that
    // without asking, so the button shows and the refusal is reported inline
    // (see MainViewModel.CanInviteDeviceToSelectedServer).
    [AvaloniaFact]
    public void An_approved_headless_server_can_be_asked_for_a_pairing_code()
    {
        var vm = Make(PairedWith("fp-desk", trustConfirmed: true));
        vm.AddOrUpdateDeviceSidebarItem(HeadlessServer("desk", "fp-desk", "Desktop"));
        vm.SelectedSidebarItem = SingleDeviceRow(vm);

        Assert.True(vm.CanInviteDeviceToSelectedServer);
    }

    // An app peer advertising Server mode has no admin API and no code to give -
    // its own Settings window is where its devices are managed.
    [AvaloniaFact]
    public void An_app_peer_in_server_mode_is_never_asked_for_a_pairing_code()
    {
        var vm = Make(PairedWith("fp-desk", trustConfirmed: true));
        vm.AddOrUpdateDeviceSidebarItem(Device("desk", "fp-desk", "Desktop", isServer: true));
        vm.SelectedSidebarItem = SingleDeviceRow(vm);

        Assert.False(vm.CanInviteDeviceToSelectedServer);
    }

    // Paired but still waiting for the server to approve this device: any admin
    // call it made would be refused, so there is nothing to offer yet.
    [AvaloniaFact]
    public void A_server_that_has_not_approved_this_device_yet_offers_nothing()
    {
        var vm = Make(PairedWith("fp-desk", trustConfirmed: false));
        vm.AddOrUpdateDeviceSidebarItem(HeadlessServer("desk", "fp-desk", "Desktop"));
        vm.SelectedSidebarItem = SingleDeviceRow(vm);

        Assert.False(vm.CanInviteDeviceToSelectedServer);
    }

    // Mobile's Settings sheet asks the paired server rather than a selected row,
    // and resolves it through PairedServerReachability - which nothing has
    // populated here, so there is no reachable server to ask.
    [AvaloniaFact]
    public void With_no_reachable_paired_server_mobile_offers_nothing()
    {
        var vm = Make(PairedWith("fp-desk", trustConfirmed: true));

        Assert.False(vm.CanInviteDeviceToPairedServer);
    }

    private static AppSettings PairedWith(string fingerprint, bool trustConfirmed) => new()
    {
        PairedServerFingerprint    = fingerprint,
        PairedServerAlias          = "Desktop",
        PairedServerTrustConfirmed = trustConfirmed,
    };

    // Device(), but advertising itself as a Flower.Server rather than another
    // copy of the app - the difference PairsByCode reads.
    private static DiscoveredDevice HeadlessServer(string instanceName, string fingerprint, string alias)
    {
        var device = Device(instanceName, fingerprint, alias, isServer: true);
        device.DeviceType = "server";
        return device;
    }
}
