using System.Collections.ObjectModel;
using System.Linq;

using Flower.Models;
using Flower.Persistence;
using Flower.Services;

using Material.Icons;

namespace Flower.ViewModels;

// What DeviceSidebarSection needs from whoever owns the sidebar, so the
// state machine below can be constructed and driven without a MainViewModel
// (see docs/ARCHITECTURE-REVIEW.md Tier 4.2, and Tier 5.6 on why reaching this
// logic through the real NetworkDiscoveryService means standing up an mDNS
// backend and an HTTP /info endpoint per case).
public interface IDeviceSidebarHost
{
    // The Client's one paired Server, or null - decides which row gets pinned,
    // which gets the syncing spinner, and which placeholder a rediscovered
    // peer is allowed to claim.
    string? PairedServerFingerprint { get; }

    // Whether any sync is currently in flight - carried onto a device row that
    // is re-created or updated mid-sync, since the spinner is only pushed on
    // IsSyncing's own edges, not whenever a row happens to change.
    bool IsSyncing { get; }

    SidebarItem? SelectedSidebarItem { get; set; }

    // Where selection lands when the currently-selected device row is removed.
    SidebarItem? DefaultSelection { get; }

    // Drop a peer from the once-per-session sync dedup set, so it syncs afresh
    // if rediscovered later this session - see MainViewModel's
    // _syncedDeviceFingerprints.
    void ForgetSyncedDevice(string fingerprint);

    // A device row's identity or display state changed in a way the
    // device-detail pane binds to. DiscoveredDevice itself doesn't raise
    // property-changed, and SidebarItem.Device can be re-pointed at a
    // different instance than the pane last read, so this is the explicit
    // nudge for SelectedDevice and the pair-button properties.
    void DeviceRowsChanged();
}

// The Devices/Server sections of the sidebar: which rows exist, which section
// each belongs to, what each is called, and which one is the pinned paired
// Server. Extracted wholesale from MainViewModel, where it was ~350 lines of
// one of that class's six unrelated jobs - it only ever touched the sidebar
// collection, the nickname store and reachability, never anything else there.
//
// Rows arrive one at a time from NetworkDiscoveryService, so these sections are
// built up live rather than as part of MainViewModel's BuildSidebarItems. A
// peer advertising Server mode (DiscoveredDevice.IsServer) goes under its own
// "Server" section instead of "Devices".
public sealed class DeviceSidebarSection
{
    private readonly ObservableCollection<SidebarItem> _items;
    private readonly IDeviceSidebarHost _host;
    private readonly DeviceNicknameStore? _nicknames;
    private readonly PairedServerReachability? _reachability;

    public DeviceSidebarSection(
        ObservableCollection<SidebarItem> items,
        IDeviceSidebarHost host,
        DeviceNicknameStore? nicknames,
        PairedServerReachability? reachability)
    {
        _items        = items;
        _host         = host;
        _nicknames    = nicknames;
        _reachability = reachability;
    }

    public void AddOrUpdate(DiscoveredDevice device)
    {
        var existing = Find(device);

        // Don't show a device under its raw mDNS instance name (e.g.
        // "localhost-iOS._flowersync._tcp.local") while its real Alias is
        // still unresolved - see ResolveAliasAsync, which re-fires
        // DeviceDiscovered once /info actually answers, so the item appears
        // here with its real name a moment later instead. Only gates
        // creating a brand new row; an already-shown row (existing != null)
        // still gets its Device reference refreshed below regardless, since
        // by definition it was already resolved once to exist at all.
        if (existing == null && string.IsNullOrEmpty(device.Fingerprint))
            return;

        if (existing != null)
        {
            existing.Device = device;
            // A device row can be re-created (RemoveDuplicates) or updated
            // while a sync with it is already in flight - carry the current
            // state forward rather than defaulting back to false, since the
            // spinner is only pushed on IsSyncing's own edges, not whenever a
            // sidebar row happens to change.
            existing.IsSyncing = device.Fingerprint == _host.PairedServerFingerprint && _host.IsSyncing;
            // Re-discovered after having gone offline while paired (see
            // RemoveItem, which keeps this row's IsPairedServer set rather
            // than removing it) - or claimed straight from MainViewModel's
            // BuildSidebarItems placeholder (IsPairedServer already true) the
            // first time this session. Either way, SyncPairedServerRow below
            // flips its glyph back to reachable now that it's live again.
            existing.IsPairedServer = device.Fingerprint == _host.PairedServerFingerprint;
            RelocateIfNeeded(existing, device);
            RemoveDuplicates(existing, device);
            RefreshDisplayNames();
            SyncPairedServerRow();
            return;
        }

        var added = new SidebarItem(SidebarItemKind.Device, ResolveDisplayName(device), IconFor(device), device: device)
        {
            IsSyncing = device.Fingerprint == _host.PairedServerFingerprint && _host.IsSyncing,
            IsPairedServer = device.Fingerprint == _host.PairedServerFingerprint,
        };
        Insert(added, device);
        RemoveDuplicates(added, device);
        RefreshDisplayNames();
        SyncPairedServerRow();
    }

    // Single place that syncs the sidebar's one pinned "paired Server" row's
    // reachability glyph from PairedServerReachability - identity (which row,
    // if any, IsPairedServer) is still set independently at each structural
    // sidebar-mutation site (MainViewModel's BuildSidebarItems/PairWithServer,
    // AddOrUpdate/RemoveItem here), since the pinned row can exist with no
    // live Device at all (see BuildSidebarItems' placeholder) - there's
    // nothing to derive identity from in that case. This only ever touches
    // whichever row already has IsPairedServer == true. Safe/cheap to call
    // unconditionally after any of those structural changes, or from
    // PairedServerReachability.Changed.
    public void SyncPairedServerRow()
    {
        var pinnedItem = _items.FirstOrDefault(i => i.Kind == SidebarItemKind.Device && i.IsPairedServer);
        if (pinnedItem != null)
            pinnedItem.IsReachable = _reachability?.IsReachable ?? false;
    }

    // Undoes PairWithServer/BuildSidebarItems' pin on whichever row was
    // showing the paired Server - called when the pairing pointer itself is
    // cleared (Unpair, or flipping this device into Server mode). A row with
    // no live Device right now (BuildSidebarItems' own placeholder, or one
    // that went offline while still pinned - see RemoveItem) has nothing left
    // to show once unpinned, so it's removed outright; one that's still
    // actually discovered just drops back to ordinary Devices/Server-section
    // behavior (the next DeviceLost for it behaves like any other
    // undiscovered peer from then on).
    public void UnpinPairedServerRow()
    {
        var pinnedItem = _items.FirstOrDefault(i => i.Kind == SidebarItemKind.Device && i.IsPairedServer);
        if (pinnedItem == null)
            return;

        if (pinnedItem.Device == null)
            RemoveItem(pinnedItem, clearSyncDedup: false);
        else
            pinnedItem.IsPairedServer = false;
    }

    // Pins a freshly-paired device's row as the paired Server (identity only -
    // SyncPairedServerRow does the reachability half).
    public void PinPairedServerRow(string fingerprint)
    {
        var item = _items.FirstOrDefault(i => i.Kind == SidebarItemKind.Device && i.Device?.Fingerprint == fingerprint);
        if (item != null)
            item.IsPairedServer = true;
    }

    // Pushes the current sync state onto whichever row represents the paired
    // Server, for that row's own spinner (see SidebarItem.IsSyncing,
    // MainView.axaml's Device row template).
    public void SetPairedServerSyncing(bool isSyncing)
    {
        var pairedFingerprint = _host.PairedServerFingerprint;
        if (pairedFingerprint == null)
            return;
        var item = _items.FirstOrDefault(i => i.Kind == SidebarItemKind.Device && i.Device?.Fingerprint == pairedFingerprint);
        if (item != null)
            item.IsSyncing = isSyncing;
    }

    private static string SectionHeaderName(DiscoveredDevice device) => device.IsServer ? "Server" : "Devices";
    private static MaterialIconKind IconFor(DiscoveredDevice device) => device.IsServer ? MaterialIconKind.Server : MaterialIconKind.Laptop;

    // Inserts a brand-new Device row into the section matching the device's
    // current role (see SectionHeaderName), creating that section's Header row
    // first if this is its first member. Appends the section itself at the end
    // of the sidebar the first time it's needed (same as the old
    // single-"Devices"-section behavior), but keeps each section's own members
    // contiguous so RelocateIfNeeded/SectionHeaderFor can find a row's section
    // by walking backward to the nearest preceding Header.
    private void Insert(SidebarItem item, DiscoveredDevice device)
    {
        var headerName = SectionHeaderName(device);
        var headerIndex = -1;
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Kind == SidebarItemKind.Header && _items[i].Name == headerName)
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
        {
            _items.Add(new SidebarItem(SidebarItemKind.Header, headerName));
            _items.Add(item);
            return;
        }

        var insertAt = headerIndex + 1;
        while (insertAt < _items.Count && _items[insertAt].Kind != SidebarItemKind.Header)
            insertAt++;
        _items.Insert(insertAt, item);
    }

    // A device's advertised role can change after its sidebar row was
    // created (e.g. the peer flips its own "Act as Server" setting) - moves
    // the row to the section matching its current role and updates its icon
    // to match, no-op if it's already in the right place. Preserves
    // selection across the move since the row is the same SidebarItem
    // instance throughout, just removed and reinserted elsewhere in the
    // collection - Remove briefly drops it out of SelectedSidebarItem via
    // the sidebar ListBox's two-way binding, so it's explicitly restored
    // after Insert if it was selected going in.
    private void RelocateIfNeeded(SidebarItem item, DiscoveredDevice device)
    {
        item.Icon = IconFor(device);

        var targetHeaderName = SectionHeaderName(device);
        var currentHeader = SectionHeaderFor(item);
        if (currentHeader?.Name == targetHeaderName)
            return;

        var wasSelected = _host.SelectedSidebarItem == item;
        _items.Remove(item);
        RemoveHeaderIfEmpty(currentHeader);
        Insert(item, device);
        if (wasSelected)
            _host.SelectedSidebarItem = item;
    }

    // The Header row immediately preceding a sidebar item, i.e. the section
    // it currently belongs to - relies on Insert always keeping a section's
    // members contiguous right after its Header.
    private SidebarItem? SectionHeaderFor(SidebarItem item)
    {
        var index = _items.IndexOf(item);
        for (var i = index - 1; i >= 0; i--)
        {
            if (_items[i].Kind == SidebarItemKind.Header)
                return _items[i];
        }
        return null;
    }

    // Drops a section's Header row once its last member is gone - shared by
    // RemoveItem (a device actually left) and RelocateIfNeeded (a device moved
    // to the other section).
    private void RemoveHeaderIfEmpty(SidebarItem? header)
    {
        if (header == null)
            return;
        var index = _items.IndexOf(header);
        if (index < 0)
            return;
        var stillHasMembers = index + 1 < _items.Count && _items[index + 1].Kind != SidebarItemKind.Header;
        if (!stillHasMembers)
            _items.Remove(header);
    }

    // A peer can transiently be discovered under more than one mDNS instance
    // name for the same physical device - e.g. a prior run's advertisement
    // wasn't cleanly withdrawn before a fresh one republished under an
    // auto-renamed instance name (Bonjour's own collision-avoidance). Each
    // shows up as its own sidebar item (via Find's InstanceName fallback,
    // since neither has a resolved Fingerprint yet to match on) until one of
    // them resolves a Fingerprint that turns out to match another
    // already-tracked item - at which point they're revealed to be duplicates
    // of the same device. Removes every OTHER Device sidebar item sharing that
    // now-resolved Fingerprint, keeping only the one AddOrUpdate just
    // added/updated.
    private void RemoveDuplicates(SidebarItem keep, DiscoveredDevice device)
    {
        if (string.IsNullOrEmpty(device.Fingerprint))
            return;

        var duplicates = _items
            .Where(i => i.Kind == SidebarItemKind.Device && i != keep && i.Device?.Fingerprint == device.Fingerprint)
            .ToList();
        foreach (var duplicate in duplicates)
            RemoveItem(duplicate, clearSyncDedup: false);
    }

    // Matches primarily by Fingerprint - the peer's own stable per-install
    // identity (see DeviceIdentityStore) - once its /info handshake has
    // resolved one, since InstanceName alone ({MachineName}-{Platform} - see
    // NetworkDiscoveryService.OwnInstanceName) can collide between two
    // genuinely distinct devices that both happen to still have the same
    // unrenamed default computer name. Matching on InstanceName regardless of
    // that would silently conflate two different devices into one sidebar
    // entry - whichever was discovered first would then keep this item's
    // Device pinned to the wrong endpoint even after its displayed name
    // updated to the second device's.
    //
    // Before a device's own Fingerprint resolves, InstanceName is the only
    // thing to go on - but such a match is only trusted against another item
    // that ALSO hasn't resolved a Fingerprint yet; an item that already has a
    // different, resolved Fingerprint is treated as a distinct device that
    // merely shares the same not-yet-renamed computer name, not the same one.
    private SidebarItem? Find(DiscoveredDevice device)
    {
        var deviceItems = _items.Where(i => i.Kind == SidebarItemKind.Device).ToList();

        if (!string.IsNullOrEmpty(device.Fingerprint))
        {
            var byFingerprint = deviceItems.FirstOrDefault(i => i.Device?.Fingerprint == device.Fingerprint);
            if (byFingerprint != null)
                return byFingerprint;

            // MainViewModel's BuildSidebarItems pins a paired-server
            // placeholder with no Device yet the first time it's actually
            // (re)discovered this session - claim it instead of creating a
            // second row for the same peer.
            if (device.Fingerprint == _host.PairedServerFingerprint)
            {
                var placeholder = deviceItems.FirstOrDefault(i => i.IsPairedServer && i.Device == null);
                if (placeholder != null)
                    return placeholder;
            }
        }

        return deviceItems.FirstOrDefault(i =>
            i.Device?.InstanceName == device.InstanceName && string.IsNullOrEmpty(i.Device.Fingerprint));
    }

    // A user-set local nickname (see DeviceNicknameStore, MainView.axaml.cs's
    // Rename Device context-menu item, TrustedDevicesView) always wins over
    // whatever the peer itself reports - otherwise the next DeviceDiscovered
    // re-fire (e.g. once /info resolves, or on periodic rediscovery) would
    // silently clobber a rename back to the peer's own alias.
    private string ResolveDisplayName(DiscoveredDevice device) =>
        !string.IsNullOrEmpty(device.Fingerprint) && _nicknames?.Get(device.Fingerprint) is { Length: > 0 } nickname
            ? nickname
            : device.Alias;

    // The single place that re-derives a Device sidebar item's displayed name
    // from ResolveDisplayName - every place a device's nickname can change
    // (the sidebar's own "Rename Device" context menu, and TrustedDevicesView's
    // pencil-icon rename) calls this afterward, so there is exactly one code
    // path computing "what do we call this device" and every UI surface (the
    // sidebar row, and the device-detail pane, which binds to
    // SelectedSidebarItem.Name - the same SidebarItem instance) reflects it
    // immediately rather than waiting for the next mDNS rediscovery to happen
    // to notice.
    public void RefreshDisplayNames()
    {
        var deviceItems = _items.Where(i => i.Kind == SidebarItemKind.Device && i.Device != null).ToList();

        foreach (var item in deviceItems)
            item.Name = ResolveDisplayName(item.Device!);

        // A subtitle (this device's IP) only shows when its name collides
        // with another currently-listed device - two distinct devices
        // legitimately sharing a display name is purely cosmetic (sync/trust
        // both key off Fingerprint, never name), but the user still needs
        // some way to tell them apart in the sidebar itself.
        foreach (var group in deviceItems.GroupBy(i => i.Name))
        {
            var showSubtitle = group.Count() > 1;
            foreach (var item in group)
                item.Subtitle = showSubtitle ? item.Device!.BaseUri.Host : null;
        }

        _host.DeviceRowsChanged();
    }

    public void Remove(string instanceName)
    {
        // mDNS's "goodbye" notification only ever carries the withdrawn
        // record's raw instance name, never a Fingerprint - so if two
        // genuinely distinct devices are colliding on InstanceName (see
        // Find), there is no way to tell from this signal alone which of them
        // actually went offline. Removing either unconditionally risks
        // dropping the one that is still there just as easily as the one that
        // isn't, so this deliberately does nothing rather than guess wrong in
        // that rare case - it will get cleaned up for real the moment a
        // Fingerprint-disambiguated event (a fresh DeviceDiscovered, or that
        // peer eventually being forgotten) sorts it out instead.
        var matches = _items.Where(i =>
            i.Kind == SidebarItemKind.Device && i.Device?.InstanceName == instanceName).ToList();
        if (matches.Count != 1)
            return;

        RemoveItem(matches[0], clearSyncDedup: true);
    }

    // Shared by Remove (a peer actually went offline, per mDNS's own goodbye
    // notification - clearSyncDedup: true, so a fresh sync fires if it's
    // discovered again later this session rather than silently being ignored
    // by the dedup check) and RemoveDuplicates (the peer never went offline -
    // it just turned out to already have another sidebar item once its
    // Fingerprint resolved, so clearSyncDedup: false: the surviving item is
    // the exact same still-present device and shares that Fingerprint,
    // clearing it here would just trigger a redundant resync of it for no
    // reason). Either way: reselect away if this item was selected, remove
    // it, and drop its section's Header row (see RemoveHeaderIfEmpty) once
    // no other Device items remain in it.
    private void RemoveItem(SidebarItem item, bool clearSyncDedup)
    {
        if (clearSyncDedup && item.Device?.Fingerprint is { Length: > 0 } fingerprint)
            _host.ForgetSyncedDevice(fingerprint);

        // The paired Server's row is pinned in place instead of
        // disappearing the moment it's no longer discovered (see
        // MainViewModel's BuildSidebarItems, PairWithServer) - a genuine
        // "peer went offline" removal (clearSyncDedup: true) just flips its
        // glyph to unreachable instead. UnpinPairedServerRow bypasses this by
        // passing clearSyncDedup: false once the pairing itself is gone, so
        // a since-unpaired row still gets removed for real below.
        if (clearSyncDedup && item.IsPairedServer)
        {
            SyncPairedServerRow();
            return;
        }

        if (_host.SelectedSidebarItem == item)
            _host.SelectedSidebarItem = _host.DefaultSelection;

        var header = SectionHeaderFor(item);
        _items.Remove(item);
        RemoveHeaderIfEmpty(header);
        RefreshDisplayNames();
    }
}
