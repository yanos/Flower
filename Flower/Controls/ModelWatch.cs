using System;
using System.ComponentModel;

using Avalonia;

namespace Flower.Controls;

// The one subscription a list row holds to the model it is showing - every row
// a list hands item after item as it scrolls: TrackRowView, AlbumCard,
// ArtistRowView, TrackDownloadButton.
//
// Those rows set what they show from code rather than binding it, because of
// how the lists showing them work: a row is built once and then handed item
// after item (RecyclingListBox, UniformRowPanel), and each binding to the item
// tears down its subscription to the last one and makes another to the next - a
// dozen per row, every time a row came on screen, which was most of what a
// fling still allocated once rows stopped being rebuilt. This is the single
// subscription that replaces them, moved from item to item, and held only
// while the row is on screen, so a long-lived model never holds a row.
//
// Whatever changes, the row is told to show everything again: setting a value
// a control already has is free, and one place that says what a row shows is
// worth more than matching each property name to its part. So the row's show
// method reads the model itself, and is all a row has to write.
public sealed class ModelWatch
{
    private readonly Action _show;
    private readonly string? _ignoring;
    private readonly PropertyChangedEventHandler _onChanged;
    private INotifyPropertyChanged? _model;
    private INotifyPropertyChanged? _subscribed;
    private bool _attached;

    // `ignoring` is one property whose changes are not worth a show - a
    // download's SpinAngle, which turns every frame and which only
    // TrackDownloadButton draws.
    public ModelWatch(Visual owner, Action show, string? ignoring = null)
    {
        _show = show;
        _ignoring = ignoring;
        _onChanged = OnChanged;
        owner.AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            Update();
            // Whatever changed while it was off screen.
            _show();
        };
        owner.DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            Update();
        };
    }

    // The model the row shows now, or null for none; shown at once.
    public void Watch(INotifyPropertyChanged? model)
    {
        _model = model;
        Update();
        _show();
    }

    private void Update()
    {
        var watch = _attached ? _model : null;
        if (ReferenceEquals(watch, _subscribed))
            return;

        if (_subscribed != null)
            _subscribed.PropertyChanged -= _onChanged;
        _subscribed = watch;
        if (watch != null)
            watch.PropertyChanged += _onChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_ignoring != null && e.PropertyName == _ignoring)
            return;
        _show();
    }
}
