using System;
using System.Collections.Generic;

using Flower.Services;
using Flower.ViewModels;

namespace Flower.Tests;

public class LogSidebarBuilderTests
{
    private static readonly Func<string, string?> NoNicknames = _ => null;

    // Nothing to ask, so nothing to show but the log this device keeps itself.
    // Same answer whether there is no paired server or the server could not be
    // reached: both arrive here as an empty roster.
    [Fact]
    public void With_no_server_roster_only_This_Device_is_listed()
    {
        var items = LogSidebarBuilder.Build([], ownFingerprint: "fp-me", NoNicknames);

        Assert.Single(items);
        Assert.Equal(LogSidebarItemKind.ThisDevice, items[0].Kind);
    }

    [Fact]
    public void A_roster_adds_the_server_itself_then_one_row_per_device_in_order()
    {
        var devices = new List<RemoteDevice>
        {
            new("fp-1", "Alias1"),
            new("fp-2", "Alias2"),
        };

        var items = LogSidebarBuilder.Build(devices, ownFingerprint: "fp-me", NoNicknames);

        Assert.Equal(4, items.Count);
        Assert.Equal(LogSidebarItemKind.ThisDevice, items[0].Kind);
        Assert.Equal(LogSidebarItemKind.Server, items[1].Kind);
        Assert.Equal(LogSidebarItemKind.PairedClient, items[2].Kind);
        Assert.Equal("fp-1", items[2].Fingerprint);
        Assert.Equal("Alias1", items[2].Name);
        Assert.Equal(LogSidebarItemKind.PairedClient, items[3].Kind);
        Assert.Equal("fp-2", items[3].Fingerprint);
    }

    // This device is on its own server's roster. Its row there would show the
    // same log "This Device" already shows live, only as stale as the last
    // sync - two rows disagreeing about one log.
    [Fact]
    public void This_devices_own_row_on_the_server_is_left_out()
    {
        var devices = new List<RemoteDevice>
        {
            new("fp-me", "This Phone"),
            new("fp-other", "Someone Else"),
        };

        var items = LogSidebarBuilder.Build(devices, ownFingerprint: "fp-me", NoNicknames);

        Assert.Equal(3, items.Count);
        Assert.DoesNotContain(items, i => i.Fingerprint == "fp-me");
        Assert.Equal("fp-other", items[2].Fingerprint);
    }

    [Fact]
    public void Nickname_override_wins_over_the_alias_the_server_reported()
    {
        var devices = new List<RemoteDevice> { new("fp-1", "StoredAlias") };
        string? Nickname(string fingerprint) => fingerprint == "fp-1" ? "My Nickname" : null;

        var items = LogSidebarBuilder.Build(devices, ownFingerprint: "fp-me", Nickname);

        Assert.Equal("My Nickname", items[2].Name);
    }
}
