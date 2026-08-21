using System;

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

// Extends ViewModelBase (not a plain record) - Alias and IsEditing both need
// settable, change-notifying properties: Alias so the row's TextBox can bind
// it, IsEditing so the row can toggle between its plain-text display and the
// pencil-clicked edit state - see TrustedDevicesView.EditAliasButton_Click.
public sealed class TrustedPeerRow : ViewModelBase
{
    public required string Fingerprint { get; init; }

    private string _alias = "";
    public string Alias
    {
        get => _alias;
        set => SetProperty(ref _alias, value);
    }

    public required DateTimeOffset ApprovedAt { get; init; }

    // Only ever true against a server, where TrustedPeer.IsAdmin decides who may
    // reach /api/admin at all. Always false for an app peer, which has no such
    // distinction - see TrustedPeer's own doc comment.
    public bool IsAdmin { get; init; }

    public string ApprovedAtDisplay =>
        IsAdmin ? $"Administrator - approved {ApprovedAt.LocalDateTime:g}" : $"Approved {ApprovedAt.LocalDateTime:g}";

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

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

// A fingerprint this device explicitly denied (or let time out unanswered -
// see SyncHttpServer.RequestApprovalAsync) rather than approved - lets the
// user see who got turned away and forget that refusal, so a since-legitimate
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
