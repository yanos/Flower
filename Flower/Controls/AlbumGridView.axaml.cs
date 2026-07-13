using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// Thin wrapper hosting AlbumGridPanel inside a ScrollViewer, same shape as
// MusicListView hosting MusicListPanel - see that control for the reasoning
// behind driving the panel's virtualization off the ScrollViewer's own
// Offset/Viewport rather than relying on ambient scroll virtualization.
// Click/multi-select/drag interaction is handled externally (MainView.axaml.cs)
// via the exposed Panel, same split as MusicListView keeps with its own panel
// internally - see MainView.axaml.cs's AlbumGrid_Pointer* handlers.
public partial class AlbumGridView : UserControl
{
    private readonly AlbumGridPanel _panel;
    private IReadOnlyList<AlbumTileViewModel> _items = Array.Empty<AlbumTileViewModel>();

    public AlbumGridPanel Panel => _panel;

    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<AlbumGridView, IEnumerable?>(nameof(ItemsSource));

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    // Album names currently selected (see MainViewModel.SelectedSubItems) -
    // drives which tiles render as selected. Not TwoWay: selection changes
    // flow the other direction, through MainViewModel.SetSelectedSubItems/
    // SelectAlbumTileCommand called from MainView.axaml.cs's pointer handlers.
    public static readonly StyledProperty<IEnumerable?> SelectedNamesProperty =
        AvaloniaProperty.Register<AlbumGridView, IEnumerable?>(nameof(SelectedNames));

    public IEnumerable? SelectedNames
    {
        get => GetValue(SelectedNamesProperty);
        set => SetValue(SelectedNamesProperty, value);
    }

    public AlbumGridView()
    {
        InitializeComponent();

        _panel = new AlbumGridPanel { VerticalAlignment = VerticalAlignment.Top };
        Scroller.Content = _panel;

        Scroller.ScrollChanged += (_, _) => SyncScroll();
        Scroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ViewportProperty)
                SyncScroll();
        };
    }

    private INotifyCollectionChanged? _observedCollection;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (_observedCollection != null)
                _observedCollection.CollectionChanged -= OnCollectionChanged;

            _observedCollection = ItemsSource as INotifyCollectionChanged;
            if (_observedCollection != null)
                _observedCollection.CollectionChanged += OnCollectionChanged;

            RefreshItems();
        }
        else if (change.Property == SelectedNamesProperty)
        {
            _panel.SetSelectedNames(SelectedNames?.Cast<string>().ToList() ?? new List<string>());
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshItems();

    private void RefreshItems()
    {
        _items = ItemsSource?.Cast<AlbumTileViewModel>().ToList() ?? new List<AlbumTileViewModel>();
        _panel.SetItems(_items);
        // A changed item count can change the total content height at the
        // same column count - re-sync so the ScrollViewer's extent updates.
        SyncScroll();
    }

    private void SyncScroll() =>
        _panel.SetViewport(Scroller.Offset.Y, Scroller.Viewport.Height, Scroller.Viewport.Width);
}
