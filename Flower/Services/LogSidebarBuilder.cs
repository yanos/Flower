using System;
using System.Collections.Generic;

using Flower.Persistence;
using Flower.ViewModels;

namespace Flower.Services;

// Pure row-building logic for the Log window's sidebar, kept separate from
// LogViewModel so the decision itself ("This Device" always, paired clients
// only when acting as a Server) is unit-testable without constructing the
// ViewModel's other dependencies - same rationale SyncRolePolicy's own doc
// comment gives for keeping that logic standalone too.
public static class LogSidebarBuilder
{
    public static List<LogSidebarItem> Build(bool isServer, IReadOnlyList<TrustedPeer> trustedPeers, Func<string, string?> resolveNickname)
    {
        var items = new List<LogSidebarItem>
        {
            new(LogSidebarItemKind.ThisDevice, "This Device")
        };

        if (isServer)
        {
            foreach (var peer in trustedPeers)
                items.Add(new LogSidebarItem(LogSidebarItemKind.PairedClient, resolveNickname(peer.Fingerprint) ?? peer.Alias, peer.Fingerprint));
        }

        return items;
    }
}
