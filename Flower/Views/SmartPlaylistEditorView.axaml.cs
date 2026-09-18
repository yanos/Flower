using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;

using Flower.ViewModels;

namespace Flower.Views;

// The smart playlist rule editor's controls, shared by the desktop window and
// the phone's sheet. Everything it decides lives in
// SmartPlaylistEditorViewModel; this is the row layout and the two clicks the
// rows make.
public partial class SmartPlaylistEditorView : UserControl
{
    // The phone's layout: a row over two lines instead of one, and no scrolling
    // of its own - see the markup.
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<SmartPlaylistEditorView, bool>(nameof(IsCompact));

    public bool IsCompact
    {
        get => GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }

    public SmartPlaylistEditorView() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsCompactProperty)
            return;

        RulesList.ItemTemplate = (IDataTemplate)Resources[IsCompact ? "CompactRow" : "WideRow"]!;
        RulesScroller.VerticalScrollBarVisibility = IsCompact
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
    }

    private SmartPlaylistEditorViewModel? ViewModel => DataContext as SmartPlaylistEditorViewModel;

    private void AddRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SmartConditionRowViewModel row })
            ViewModel?.AddCondition(row);
    }

    private void RemoveRow_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: SmartConditionRowViewModel row })
            ViewModel?.RemoveCondition(row);
    }
}
