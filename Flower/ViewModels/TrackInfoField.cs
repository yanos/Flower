using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Services;

namespace Flower.ViewModels;

// One tag box in Track Info, on either head: the tag it edits
// (TrackTagFields), the text in it, and what it showed when the songs were
// loaded. A box whose text still matches is not saved, which is what leaves
// "Multiple values" alone across several songs.
public sealed class TrackInfoField(TrackTagField tag) : ViewModelBase
{
    public TrackTagField Tag { get; } = tag;

    private string _text = "";

    // Two-way bound to the box. Compared with what was shown at save time,
    // never tracked through change events: a TextBox raises TextChanged later
    // than the assignment that caused it, so a "loading" guard has already
    // been lowered by then and every box would read as edited.
    public string Text
    {
        get => _text;
        set => SetProperty(ref _text, value ?? "");
    }

    private string? _placeholder;

    // "Multiple values" when the songs disagree; otherwise the hint the view
    // model gives the box (a sort tag's own name, say), or none.
    public string? Placeholder
    {
        get => _placeholder;
        private set => SetProperty(ref _placeholder, value);
    }

    public bool IsMixed { get; private set; }

    public string ShownText { get; private set; } = "";

    // An empty box over songs that disagree means "leave each as it is", so
    // emptying the field for all of them needs a way of its own to be said
    // (UF-14): the box's clear button, offered while it shows "Multiple
    // values". Cleared, it saves as empty for every song, until something is
    // typed in it.
    private bool _isCleared;
    public bool IsCleared
    {
        get => _isCleared;
        private set
        {
            if (!SetProperty(ref _isCleared, value))
                return;
            OnPropertyChanged(nameof(OffersClear));
            _clear?.NotifyCanExecuteChanged();
        }
    }

    public bool OffersClear => IsMixed && !IsCleared;

    private RelayCommand? _clear;
    public ICommand ClearCommand => _clear ??= new RelayCommand(Clear, () => OffersClear);

    public bool IsDirty => IsCleared || Text != ShownText;

    public void Clear()
    {
        if (!OffersClear)
            return;
        Text = "";
        IsCleared = true;
        Placeholder = TrackInfoViewModel.ClearedValues;
    }

    internal void Show(string? common, string? hint)
    {
        IsMixed = common is null;
        ShownText = common ?? "";
        Text = ShownText;
        IsCleared = false;
        OnPropertyChanged(nameof(OffersClear));
        _clear?.NotifyCanExecuteChanged();
        Placeholder = IsMixed ? TrackInfoViewModel.MixedValues : hint;
    }
}
