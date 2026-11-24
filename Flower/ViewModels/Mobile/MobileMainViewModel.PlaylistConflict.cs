using System;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Services;

namespace Flower.ViewModels.Mobile;

// The phone's version of PlaylistConflictWindow: the same playlist changed here
// and on the server, or deleted there with no way to tell whether this copy
// holds an edit, and only the person can say which to keep. Every way out of
// the sheet gives an answer.
public partial class MobileMainViewModel
{
    private PlaylistConflictEventArgs? _pendingConflict;

    public bool IsShowingPlaylistConflict => ActiveSheet == MobileSheet.PlaylistConflict;

    // What the sheet says, or null while it asks nothing.
    public PlaylistConflictText? PlaylistConflict { get; private set; }

    private ICommand? _keepLocalPlaylist;
    public ICommand KeepLocalPlaylistCommand => _keepLocalPlaylist ??=
        new RelayCommand(() => AnswerPlaylistConflict(PlaylistConflictChoice.KeepLocal));

    private ICommand? _keepRemotePlaylist;
    public ICommand KeepRemotePlaylistCommand => _keepRemotePlaylist ??=
        new RelayCommand(() => AnswerPlaylistConflict(PlaylistConflictChoice.KeepRemote));

    private ICommand? _decidePlaylistConflictLater;
    public ICommand DecidePlaylistConflictLaterCommand => _decidePlaylistConflictLater ??=
        new RelayCommand(() => AnswerPlaylistConflict(PlaylistConflictChoice.Later));

    // Never over a sheet the person is using: that one is not theirs to lose
    // to a background sync, and the next session asks again.
    internal void AskAboutPlaylistConflict(PlaylistConflictEventArgs e)
    {
        if (ActiveSheet != MobileSheet.None)
        {
            e.Resolution.TrySetResult(PlaylistConflictChoice.NotAsked);
            return;
        }

        _pendingConflict = e;
        PlaylistConflict = PlaylistConflictText.Of(e);
        OnPropertyChanged(nameof(PlaylistConflict));
        ActiveSheet = MobileSheet.PlaylistConflict;
    }

    private void AnswerPlaylistConflict(PlaylistConflictChoice choice)
    {
        ResolvePendingConflict(choice);
        ActiveSheet = MobileSheet.None;
    }

    // Answers at most once: closing the sheet comes back through here with
    // the answer already given.
    private void ResolvePendingConflict(PlaylistConflictChoice choice)
    {
        var pending = _pendingConflict;
        _pendingConflict = null;
        pending?.Resolution.TrySetResult(choice);
    }
}
