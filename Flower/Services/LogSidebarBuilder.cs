using System;
using System.Collections.Generic;
using System.Linq;

using Flower.ViewModels;

namespace Flower.Services;

// Pure row-building logic for the Log window's sidebar, kept separate from
// LogViewModel so the decision itself is unit-testable without constructing
// the ViewModel's other dependencies - same rationale SyncRolePolicy's own doc
// comment gives for keeping that logic standalone too.
//
// "This Device" is always there and always live. Everything else only exists
// once this device is paired with a server, and comes from that server: its
// own log, and the last snapshot each device paired with it pushed. That is
// the shape of the feature - the owner of the server diagnosing a listener's
// phone from their own machine - and it is why the roster is the server's
// rather than anything held locally.
public static class LogSidebarBuilder
{
    public static List<LogSidebarItem> Build(
        IReadOnlyList<RemoteDevice> serverDevices,
        string? ownFingerprint,
        Func<string, string?> resolveNickname)
    {
        var items = new List<LogSidebarItem>
        {
            new(LogSidebarItemKind.ThisDevice, "This Device"),
        };

        if (serverDevices.Count == 0)
            return items;

        items.Add(new LogSidebarItem(LogSidebarItemKind.Server, "Server"));

        // This device is on the server's roster too, and its row there would
        // show the same lines "This Device" already does, only as stale as the
        // last sync. Two rows disagreeing about one log is worse than one.
        foreach (var device in serverDevices.Where(d => d.Fingerprint != ownFingerprint))
            items.Add(new LogSidebarItem(LogSidebarItemKind.PairedClient,
                resolveNickname(device.Fingerprint) ?? device.Alias, device.Fingerprint));

        return items;
    }
}

// The subset of a server's device roster this needs, so the builder stays a
// pure function over data rather than over an admin-API DTO.
public sealed record RemoteDevice(string Fingerprint, string Alias);
