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

    public bool IsDirty => Text != ShownText;

    internal void Show(string? common, string? hint)
    {
        IsMixed = common is null;
        ShownText = common ?? "";
        Text = ShownText;
        Placeholder = IsMixed ? TrackInfoViewModel.MixedValues : hint;
    }
}
