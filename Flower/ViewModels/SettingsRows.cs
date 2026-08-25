using System;
using System.Collections.Generic;

namespace Flower.ViewModels;

// The row types the settings screens bind to. They live here rather than beside
// the controls that render them (TrustedPeerRow and DeniedPeerRow used to sit in
// TrustedDevicesView.axaml.cs, LibraryPathRow in SettingsWindow.axaml.cs) because
// there are now two screens rendering them - the desktop Settings window and the
// server's browser settings page - and two ISettingsBackend implementations
// producing them.

// One row in the library-folders list. SongCount is how many of the library's
// current tracks live under Path, so a user can tell at a glance whether a folder
// actually contributed anything to the scan. Negative means "not counted" - the
// remote/server backend has no cheap way to attribute a track to a folder without
// pulling the whole catalog over the wire, and a wrong count is worse than none.
public sealed record LibraryPathRow(string Path, int SongCount)
{
    public bool HasSongCount => SongCount >= 0;
    public string SongCountDisplay => SongCount switch
    {
        < 0 => "",
        1 => "1 song",
        _ => $"{SongCount:N0} songs",
    };
}

// Extends ViewModelBase (not a plain record) for IsConfirmingForget alone - the
// inline "Forget?" state below needs to notify. Alias is the name the peer
// reported for itself at pairing time and is shown as-is: the roster only ever
// renders against a server, which has no alias override to write.
public sealed class TrustedPeerRow : ViewModelBase
{
    public required string Fingerprint { get; init; }

    public required string Alias { get; init; }

    public required DateTimeOffset ApprovedAt { get; init; }

    // Only ever true against a server, where TrustedPeer.IsAdmin decides who may
    // reach /api/admin at all. Always false for an app peer, which has no such
    // distinction - see TrustedPeer's own doc comment.
    public bool IsAdmin { get; init; }

    public string ApprovedAtDisplay =>
        IsAdmin ? $"Administrator - approved {ApprovedAt.LocalDateTime:g}" : $"Approved {ApprovedAt.LocalDateTime:g}";

    // Set while this row is asking "forget this device?" in place. An inline
    // confirmation rather than a modal: this list is rendered both in a desktop
    // window and in the browser, where Avalonia is single-view and there is no
    // Window to own a dialog at all (which is what used to make Forget silently
    // do nothing outside a window).
    private bool _isConfirmingForget;
    public bool IsConfirmingForget
    {
        get => _isConfirmingForget;
        set
        {
            if (SetProperty(ref _isConfirmingForget, value))
                OnPropertyChanged(nameof(IsNotConfirmingForget));
        }
    }

    public bool IsNotConfirmingForget => !_isConfirmingForget;
}

// A fingerprint a server explicitly denied (or let time out unanswered) rather
// than approved - lets the owner see who got turned away and forget that
// refusal, so a since-legitimate
// device isn't left permanently unable to re-request (denials aren't
// re-prompted from scratch, but nothing here blocks a fresh pair-request
// either way - this list is purely visibility/cleanup, see
// TrustedPeerStore.DenyAsync's own doc comment). No rename affordance - a
// denied peer has no ongoing relationship to nickname.
public sealed class DeniedPeerRow
{
    public required string Fingerprint { get; init; }
    public required string Alias { get; init; }
    public required DateTimeOffset DeniedAt { get; init; }
    public string DeniedAtDisplay => $"Denied {DeniedAt.LocalDateTime:g}";
}

// A credential minted for a third-party Subsonic client - SYNC-PLAN.md's path B,
// for clients that cannot hold a keypair. Password is non-null exactly once, in
// the response that created it: the server does not store it retrievably, so the
// page either shows it now or the user issues a new one.
public sealed class SubsonicCredentialRow : ViewModelBase
{
    public required string Username { get; init; }
    public required string Label { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public string? Password { get; init; }

    public bool HasPassword => !string.IsNullOrEmpty(Password);

    public string Detail => LastSeenAt is { } seen
        ? $"Created {CreatedAt.LocalDateTime:g} - last seen {seen.LocalDateTime:g}"
        : $"Created {CreatedAt.LocalDateTime:g} - never used";
}

// One row in the Logs tab's list of whose log to read: the server itself, or a
// device on its roster. A null Fingerprint is the server - it answers its own
// admin route rather than one keyed by fingerprint, and it is the row the tab
// lands on.
public sealed record LogSourceRow(string Name, string? Fingerprint);

// How an origin names the server, which is what the General tab groups the
// address list by: an IPv6 literal and the IPv4 one beside it are the same
// server reached two ways, and reading a flat list of them means checking each
// string character by character to work out which is which.
public enum ServerAddressKind
{
    IPv4,
    IPv6,
    Hostname,
}

// One origin this server can be dialled at, as shown on the General tab. Address
// is what the row displays and what a client types in, scheme and all
// ("https://192.168.1.5:4534") - which of the two schemes a row is decides what
// an operator hands out (see DiscoveryEndpoints.ReachableOrigins), so it is not
// something to leave to an icon. The derived pieces group and decorate it.
public sealed record ServerAddressRow(string Address)
{
    private Uri? Parsed => Uri.TryCreate(Address, UriKind.Absolute, out var uri) ? uri : null;

    public bool IsSecure => Parsed?.Scheme == Uri.UriSchemeHttps;

    // Anything that isn't a literal address - a .ts.net name, the operator's own
    // AdvertisedHost, an origin that didn't parse at all - is a hostname as far as
    // this list is concerned: none of them commit to a version.
    public ServerAddressKind Kind => Parsed?.HostNameType switch
    {
        UriHostNameType.IPv4 => ServerAddressKind.IPv4,
        UriHostNameType.IPv6 => ServerAddressKind.IPv6,
        _ => ServerAddressKind.Hostname,
    };

    // Null for anything that didn't parse - HyperlinkButton needs a real Uri, and
    // a dead link is worse than plain text.
    public Uri? NavigateUri => Parsed;
}

// One heading plus the origins under it - see SettingsViewModel.AddressGroups.
// Only groups with something in them are built, so a server with no IPv6 never
// shows an empty IPv6 heading.
public sealed record ServerAddressGroup(string Title, IReadOnlyList<ServerAddressRow> Addresses);
