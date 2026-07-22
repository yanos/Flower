using System;
using System.Collections.Generic;

using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Tests;

public class LogSidebarBuilderTests
{
    private static readonly Func<string, string?> NoNicknames = _ => null;

    [Fact]
    public void Non_server_instance_only_shows_This_Device_regardless_of_trusted_peer_count()
    {
        var peers = new List<TrustedPeer>
        {
            new("fp-1", "Alias1", DateTimeOffset.UtcNow),
            new("fp-2", "Alias2", DateTimeOffset.UtcNow)
        };

        var items = LogSidebarBuilder.Build(isServer: false, peers, NoNicknames);

        Assert.Single(items);
        Assert.Equal(LogSidebarItemKind.ThisDevice, items[0].Kind);
    }

    [Fact]
    public void Server_instance_shows_This_Device_plus_one_row_per_trusted_peer_in_order()
    {
        var peers = new List<TrustedPeer>
        {
            new("fp-1", "Alias1", DateTimeOffset.UtcNow),
            new("fp-2", "Alias2", DateTimeOffset.UtcNow)
        };

        var items = LogSidebarBuilder.Build(isServer: true, peers, NoNicknames);

        Assert.Equal(3, items.Count);
        Assert.Equal(LogSidebarItemKind.ThisDevice, items[0].Kind);
        Assert.Equal(LogSidebarItemKind.PairedClient, items[1].Kind);
        Assert.Equal("fp-1", items[1].Fingerprint);
        Assert.Equal("Alias1", items[1].Name);
        Assert.Equal(LogSidebarItemKind.PairedClient, items[2].Kind);
        Assert.Equal("fp-2", items[2].Fingerprint);
    }

    [Fact]
    public void Nickname_override_wins_over_the_peers_stored_alias()
    {
        var peers = new List<TrustedPeer> { new("fp-1", "StoredAlias", DateTimeOffset.UtcNow) };
        string? Nickname(string fingerprint) => fingerprint == "fp-1" ? "My Nickname" : null;

        var items = LogSidebarBuilder.Build(isServer: true, peers, Nickname);

        Assert.Equal("My Nickname", items[1].Name);
    }

    [Fact]
    public void Server_instance_with_no_trusted_peers_still_shows_only_This_Device()
    {
        var items = LogSidebarBuilder.Build(isServer: true, new List<TrustedPeer>(), NoNicknames);

        Assert.Single(items);
        Assert.Equal(LogSidebarItemKind.ThisDevice, items[0].Kind);
    }
}
