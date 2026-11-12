using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Microsoft.Extensions.Logging;

using Flower.Models;
using Flower.Services;

namespace Flower.ViewModels.Mobile;

// Remove from Library on the phone: a song's, an album's or a screen's,
// confirmed through one sheet with an "also delete the files" choice - the
// wording is LibraryRemovalPrompt's, shared with the desktop dialog. Its own
// class rather than a region of MobileMainViewModel
// (docs/CODE-REVIEW-2026-09.md E3). What it needs of the shell is the sheet:
// Ask puts it up, an answer takes it down, and the shell calls Settle whenever
// it comes down by any other door.
//
// A null service is a head with no peer stack, which offers no entry at all
// (Ask does nothing, and the menus ask Service before listing the entry).
public sealed class MobileLibraryRemovalViewModel : ViewModelBase
{
    private readonly Action<MobileSheet> _setSheet;
    private readonly ILogger _logger;
    private LibraryRemovalPrompt? _prompt;

    public MobileLibraryRemovalViewModel(LibraryRemovalService? service, Action<MobileSheet> setSheet, ILogger logger)
    {
        Service = service;
        _setSheet = setSheet;
        _logger = logger;

        ConfirmCommand = new RelayCommand(async () => await ConfirmAsync());
        CancelCommand = new RelayCommand(() => _setSheet(MobileSheet.None));
    }

    public LibraryRemovalService? Service { get; }

    public string Title => _prompt?.Title ?? "";
    public string Message => _prompt?.Message ?? "";
    public string DeleteFilesLabel => _prompt?.DeleteFilesLabel ?? "";
    public bool OffersDeleteFiles => _prompt?.DeleteFilesLabel != null;

    // Off every time the sheet opens: deleting files is the choice to make on
    // purpose, never one left ticked from last time.
    private bool _deleteFiles;
    public bool DeleteFiles
    {
        get => _deleteFiles;
        set => SetProperty(ref _deleteFiles, value);
    }

    // What went wrong with the last attempt, shown in the sheet rather than
    // closing it - "your server isn't reachable" is worth reading before the
    // sheet goes away.
    private string? _error;
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error != null;

    private bool _isRemoving;
    public bool IsRemoving
    {
        get => _isRemoving;
        private set => SetProperty(ref _isRemoving, value);
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }

    public void Ask(IReadOnlyList<Track> tracks)
    {
        if (Service == null || LibraryRemovalPrompt.For(Service, tracks) is not { } prompt)
            return;

        _prompt = prompt;
        DeleteFiles = false;
        Error = null;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(DeleteFilesLabel));
        OnPropertyChanged(nameof(OffersDeleteFiles));
        _setSheet(MobileSheet.ConfirmRemoveFromLibrary);
    }

    // The sheet is down, whichever way it went: nothing is pending any more.
    public void Settle()
    {
        _prompt = null;
        Error = null;
    }

    private async Task ConfirmAsync()
    {
        if (Service == null || _prompt is not { } prompt || IsRemoving)
            return;

        IsRemoving = true;
        try
        {
            var outcome = await Service.RemoveAsync(prompt.Tracks, DeleteFiles);
            if (outcome.Error is { } error && outcome.Removed == 0)
            {
                Error = error;
                return;
            }
            if (outcome.Error is { } partial)
                _logger.LogWarning("Remove from Library: {Problem}", partial);
            _setSheet(MobileSheet.None);
        }
        finally
        {
            IsRemoving = false;
        }
    }
}
