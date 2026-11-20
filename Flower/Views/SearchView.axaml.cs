using System;
using System.Collections.Generic;
using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Services;
using Flower.ViewModels;
using Flower.ViewModels.Mobile;

namespace Flower.Views;

// See the markup. A search is remembered as the phone remembers one: when the
// box is left with something in it, Return pressed in it, or a result opened -
// never on each keystroke, or every prefix of a word would be kept as a
// search of its own.
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
    }

    private MainViewModel? Main => DataContext as MainViewModel;

    private SearchViewModel? _search;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_search is not null)
            _search.PropertyChanged -= Search_PropertyChanged;
        _search = Main?.Search;
        if (_search is not null)
            _search.PropertyChanged += Search_PropertyChanged;
    }

    // ── The box's suggestions ────────────────────────────────────────────
    //
    // Handed to the box only when it asks (Populating), which is when what is
    // in it was typed or set - never when a suggestion put it there. Bound
    // straight to Search.Suggestions, picking one went wrong: the pick is a
    // new query, so a new list followed 150ms later; a new list empties the
    // box's dropdown, which drops the row it had picked; and a box whose
    // picked row goes puts back what it last had typed (its SearchText). So
    // the pick was replaced by the words it was picked for, and walking the
    // list with the arrows snapped back at every step. Remembering the search
    // and a rescan refresh the list too, and were the same trap.

    // The box has asked and is waiting for SearchViewModel's next list.
    private bool _awaitingSuggestions;

    private void Box_Populating(object? sender, PopulatingEventArgs e)
    {
        e.Cancel = true;
        // Blank is answered at once, with nothing (SearchViewModel's
        // ScheduleSuggestions) - before the box asked, so no list follows.
        if (string.IsNullOrWhiteSpace(e.Parameter))
        {
            _awaitingSuggestions = false;
            Fill([]);
            return;
        }
        _awaitingSuggestions = true;
    }

    // A row picked or walked to ends the wait. The pick is a new query, whose
    // list would otherwise be the one handed over - and a new list drops the
    // picked row, putting back what was typed. That happened whenever a row
    // was picked from the list on show before the one for the last key had
    // arrived, inside SuggestionDelay of typing.
    private void Box_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Box.SelectedItem is not null)
            _awaitingSuggestions = false;
    }

    // Every query change starts a fresh list and cancels the one before, so
    // a list arriving while the box waits is the list for what is in it.
    private void Search_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchViewModel.Suggestions) || !_awaitingSuggestions || _search is null)
            return;
        _awaitingSuggestions = false;
        Fill(_search.Suggestions);
    }

    private void Fill(IReadOnlyList<SearchSuggestion> suggestions)
    {
        Box.ItemsSource = suggestions;
        Box.PopulateComplete();
    }

    // From the container's factory the view model carries, as every view
    // XAML instantiates reaches its logger.
    private ILogger Logger => Main!.LoggerFactory.CreateLogger<SearchView>();

    // Cmd/Ctrl+F and the View menu's Search: the page shown, the box ready to
    // type in. Posted, since the page was hidden a moment ago.
    public void FocusBox() => Dispatcher.UIThread.Post(() => Box.Focus());

    private void Box_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Main?.Search.Remember();
    }

    private void Box_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (Main?.Search is { HasQuery: true } search)
            search.Remember();
    }

    private void Album_Click(object? sender, RoutedEventArgs e)
    {
        if (Main is not { } vm || (sender as Control)?.DataContext is not AlbumTileViewModel tile)
            return;
        vm.Search.Remember();
        vm.ShowAlbumAsync(tile).Forget(Logger, "Opening an album from Search");
    }

    private void Artist_Click(object? sender, RoutedEventArgs e)
    {
        if (Main is not { } vm || (sender as Control)?.DataContext is not ArtistPickerRow artist)
            return;
        vm.Search.Remember();
        vm.ShowArtistAsync(artist.Name).Forget(Logger, "Opening an artist from Search");
    }
}
