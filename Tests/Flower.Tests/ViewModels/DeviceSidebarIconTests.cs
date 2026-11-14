using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

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
public class DeviceSidebarIconTests
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

    // A server's row shows its own icon: the paired server's as this device
    // keeps it, and any other server's emoji as its /info gave it.
    [Fact]
    public void A_server_row_shows_that_servers_icon()
    {
        var items = new ObservableCollection<SidebarItem> { new(SidebarItemKind.Songs, "Songs") };
        var host = new Host { DefaultSelection = items[0], PairedServerFingerprint = "paired-fp" };
        var settings = new Flower.Persistence.AppSettings { PairedServerFingerprint = "paired-fp", PairedServerIconEmoji = "🏠" };
        using var http = new System.Net.Http.HttpClient();
        var pairedIcon = new PairedServerIcon(settings, null, new TestSupport.StaticPeerCredentials("X-Test", "1"),
            http, NullLogger<PairedServerIcon>.Instance,
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
}
