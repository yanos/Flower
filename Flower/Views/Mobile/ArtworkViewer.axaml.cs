using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Flower.Views.Mobile;

// A cover on the whole screen, black around it: what tapping the cover in
// Track Info opens, and where a cover Find Online came back with is shown to
// be taken or passed over (OffersChoice).
//
// Placed on the window's overlay layer rather than inside the sheet, so it
// covers the status bar and the home indicator too - the sheet itself stops
// below the one, and every view under it would show around the picture.
public partial class ArtworkViewer : UserControl
{
    private OverlayLayer? _layer;
    private Bitmap? _picture;

    // Use This Cover, and Try Another - only offered with OffersChoice.
    public event EventHandler? Accepted;
    public event EventHandler? NextRequested;
    public event EventHandler? Closed;

    public ArtworkViewer()
    {
        InitializeComponent();
        Tapped += Viewer_Tapped;
    }

    public bool IsOpen => _layer != null;

    // A cover to choose rather than to look at: the buttons to take it or see
    // the next one, and a tap on the picture no longer closes it.
    public bool OffersChoice
    {
        get => Choices.IsVisible;
        set => Choices.IsVisible = value;
    }

    // Try Another, only while there is another to try; Use This Cover takes
    // the row on its own otherwise.
    public void SetHasNext(bool hasNext)
    {
        NextButton.IsVisible = hasNext;
        Grid.SetColumn(UseButton, hasNext ? 1 : 0);
        Grid.SetColumnSpan(UseButton, hasNext ? 1 : 2);
    }

    // The picture, which this now owns and disposes when it is replaced or
    // the viewer closes - always a decode of its own, never a bitmap the
    // album-art cache hands out to every view.
    public void ShowPicture(Bitmap picture, string caption, string? detail = null)
    {
        ReplacePicture(picture);
        Status.IsVisible = false;
        Caption.Text = caption;
        Detail.Text = detail;
        Detail.IsVisible = !string.IsNullOrEmpty(detail);
        SetBusy(false);
    }

    // Words where the picture goes - "Downloading…", or why there is none.
    public void ShowStatus(string status)
    {
        ReplacePicture(null);
        Status.Text = status;
        Status.IsVisible = true;
        Caption.Text = "";
        Detail.IsVisible = false;
    }

    public void SetBusy(bool busy)
    {
        UseButton.IsEnabled = !busy;
        NextButton.IsEnabled = !busy;
    }

    public void Open(Visual anchor)
    {
        if (_layer != null || OverlayLayer.GetOverlayLayer(anchor) is not { } layer)
            return;

        _layer = layer;
        // The overlay layer is a Canvas: a child is as big as it says it is.
        Fit();
        layer.PropertyChanged += Layer_PropertyChanged;
        layer.Children.Add(this);

        if (TopLevel.GetTopLevel(anchor)?.InsetsManager?.SafeAreaPadding is { } safeArea)
            Frame.Margin = safeArea;

        Dispatcher.UIThread.Post(() => Opacity = 1);
    }

    public void Close()
    {
        if (_layer is not { } layer)
            return;

        _layer = null;
        layer.PropertyChanged -= Layer_PropertyChanged;
        layer.Children.Remove(this);
        ReplacePicture(null);
        Opacity = 0;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void ReplacePicture(Bitmap? picture)
    {
        var old = _picture;
        _picture = picture;
        Picture.Source = picture;
        old?.Dispose();
    }

    private void Layer_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
            Fit();
    }

    private void Fit()
    {
        if (_layer is not { } layer)
            return;
        Width = layer.Bounds.Width;
        Height = layer.Bounds.Height;
    }

    // Anywhere on the screen, picture or not, as a photo viewer closes.
    private void Viewer_Tapped(object? sender, TappedEventArgs e)
    {
        if (OffersChoice)
            return;
        e.Handled = true;
        Close();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Use_Click(object? sender, RoutedEventArgs e) => Accepted?.Invoke(this, EventArgs.Empty);

    private void Next_Click(object? sender, RoutedEventArgs e) => NextRequested?.Invoke(this, EventArgs.Empty);
}
