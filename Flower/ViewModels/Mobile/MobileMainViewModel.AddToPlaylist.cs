using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

namespace Flower.ViewModels.Mobile;

// ── Add to Playlist ───────────────────────────────────────────────────
//
// The sheet's Recent section, and the question asked when a song going into
// a playlist is in it already.
public partial class MobileMainViewModel
{
    // How many playlists the sheet's Recent section offers.
    private const int RecentPlaylistCount = 2;

    // The playlists last changed - a song added, a rename, an edit synced in -
    // at the top of the sheet, since the one a song goes into is most often
    // the one the last went into. Only playlists a song can go into, and only
    // when there are more of those than this shows: with two or fewer, every
    // one of them is at hand in the list already. Read as the sheet opens
    // (ActiveSheet raises it), not kept up to date while it is up.
    public IReadOnlyList<SidebarItem> RecentPlaylistTargets
    {
        get
        {
            var playlists = Main.SidebarItems.Where(i => i.Playlist is { IsOrdinary: true }).ToList();
            if (playlists.Count <= RecentPlaylistCount)
                return [];
            return [.. playlists.OrderByDescending(i => i.Playlist!.UpdatedAt).Take(RecentPlaylistCount)];
        }
    }

    public bool HasRecentPlaylistTargets => RecentPlaylistTargets.Count > 0;

    // "Add it again?" - the phone's answer to the question
    // PlaylistManagementViewModel.AddTracks asks of a song the playlist holds
    // already (desktop's is a dialog raised from MainView). Its words are the
    // question's own, so the two heads say the same thing.
    private DuplicateTracksConfirmationEventArgs? _pendingDuplicates;

    public string ConfirmAddDuplicatesTitle => _pendingDuplicates?.Title ?? "";
    public string ConfirmAddDuplicatesMessage => _pendingDuplicates?.Message ?? "";
    public string ConfirmAddDuplicatesText => _pendingDuplicates?.ConfirmText ?? "";
    public string DeclineAddDuplicatesText => _pendingDuplicates?.DeclineText ?? "";

    public bool IsShowingConfirmAddDuplicates => ActiveSheet == MobileSheet.ConfirmAddDuplicates;

    private void AskToAddDuplicates(DuplicateTracksConfirmationEventArgs e)
    {
        // One question at a time; a second is answered no, as a dismissal is.
        ResolvePendingDuplicates(added: false);
        _pendingDuplicates = e;
        OnPropertyChanged(nameof(ConfirmAddDuplicatesTitle));
        OnPropertyChanged(nameof(ConfirmAddDuplicatesMessage));
        OnPropertyChanged(nameof(ConfirmAddDuplicatesText));
        OnPropertyChanged(nameof(DeclineAddDuplicatesText));
        ActiveSheet = MobileSheet.ConfirmAddDuplicates;
    }

    // Answers at most once, as ResolvePendingPlaylistDeletion does: closing
    // the sheet by any way but its first button is a no.
    private void ResolvePendingDuplicates(bool added)
    {
        var pending = _pendingDuplicates;
        _pendingDuplicates = null;
        pending?.Confirmed.TrySetResult(added);
    }

    public ICommand ConfirmAddDuplicatesCommand => _confirmAddDuplicates ??= new RelayCommand(() =>
    {
        ResolvePendingDuplicates(added: true);
        ActiveSheet = MobileSheet.None;
    });

    public ICommand DeclineAddDuplicatesCommand => _declineAddDuplicates ??= new RelayCommand(() =>
    {
        ResolvePendingDuplicates(added: false);
        ActiveSheet = MobileSheet.None;
    });

    private ICommand? _confirmAddDuplicates;
    private ICommand? _declineAddDuplicates;
}
