using System;
using System.Collections.Generic;
using System.Linq;

using CommunityToolkit.Mvvm.Input;

using Flower.Persistence;

namespace Flower.ViewModels;

// Says the library holds some songs more than once, with a button to the page
// listing them. Across a desktop's page and atop a phone's Home. Either answer
// covers the songs it was about (AppSettings.DismissedDuplicateSongs), so it
// comes back only for a duplicate it has not mentioned yet.
public sealed partial class DuplicateSongsNoticeViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Action _save;
    private readonly Action _show;
    private IReadOnlyList<string> _keys = [];

    public DuplicateSongsNoticeViewModel(AppSettings settings, Action save, Action show)
    {
        _settings = settings;
        _save = save;
        _show = show;
    }

    public void Update(IReadOnlyList<string> keys)
    {
        _keys = keys;
        var answered = _settings.DismissedDuplicateSongs.ToHashSet();
        IsOpen = keys.Any(key => !answered.Contains(key));
        OnPropertyChanged(nameof(Message));
    }

    public string Message => _keys.Count == 1
        ? "1 song is in your library more than once."
        : $"{_keys.Count:N0} songs are in your library more than once.";

    private bool _isOpen;
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    [RelayCommand]
    private void Show()
    {
        Dismiss();
        _show();
    }

    [RelayCommand]
    private void Dismiss()
    {
        _settings.DismissedDuplicateSongs = _keys.ToList();
        _save();
        IsOpen = false;
    }
}
