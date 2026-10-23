using System;
using System.IO;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

using Flower.Services;

namespace Flower.Views;

// A cover on the whole screen, black around it: what tapping the cover in
// the phone's Track Info opens, and where the covers Find Online came back
// with are shown one at a time, on either head, stepped through by the
// arrows, a swipe or the arrow keys, to be taken or passed over
// (OffersChoice - see OnlineCoverPicker).
//
// Placed on the window's overlay layer rather than inside the sheet, so it
// covers the status bar and the home indicator too - the sheet itself stops
// below the one, and every view under it would show around the picture. On
// the desktop the window is Track Info's own, so it fills that window.
public partial class ArtworkViewer : UserControl
{
    private OverlayLayer? _layer;
    private Bitmap? _picture;

    // Use This Cover, and the cover either side of this one - only offered
    // with OffersChoice.
    public event EventHandler? Accepted;
    public event EventHandler? PreviousRequested;
    public event EventHandler? NextRequested;
    public event EventHandler? Closed;

    // How far a finger has to travel sideways, and more sideways than down,
    // to be a swipe to the next cover rather than a tap or a wobble.
    private const double SwipeDistance = 50;

    private int _index;
    private int _count;
    private bool _busy;
    private Point? _swipeStart;

    public ArtworkViewer()
    {
        InitializeComponent();
        Tapped += Viewer_Tapped;
        // Handled ones too: a swipe that starts on a button is still a swipe.
        AddHandler(PointerPressedEvent, Viewer_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, Viewer_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        KeyDown += Viewer_KeyDown;
    }

    // A picture for the full screen: its own size, or 2048 wide where it is
    // wider - sharp on any phone and most monitors, without holding a
    // 3000-pixel decode. The size is the picture's own, for the caption.
    private const int ScreenPixels = 2048;

    public static (Bitmap Bitmap, PixelSize Size)? DecodeForScreen(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            // The size from the header where it can be read, so the picture is
            // decoded once, at the size it is shown.
            if (ImageHeader.TryReadSize(stream) is { } header)
            {
                var wanted = new PixelSize(header.Width, header.Height);
                return (wanted.Width <= ScreenPixels ? new Bitmap(stream) : Bitmap.DecodeToWidth(stream, ScreenPixels), wanted);
            }

            var full = new Bitmap(stream);
            var size = full.PixelSize;
            if (size.Width <= ScreenPixels)
                return (full, size);

            full.Dispose();
            stream.Position = 0;
            return (Bitmap.DecodeToWidth(stream, ScreenPixels), size);
        }
        catch
        {
            return null;
        }
    }

    public bool IsOpen => _layer != null;

    // A cover to choose rather than to look at: the buttons to take it or see
    // the next one, and a tap on the picture no longer closes it.
    public bool OffersChoice
    {
        get => Choices.IsVisible;
        set => Choices.IsVisible = value;
    }

    // Which of how many covers is showing. The arrows only when there is more
    // than one, each greyed at its end - Use This Cover takes the row on its
    // own otherwise.
    public void SetPosition(int index, int count)
    {
        _index = index;
        _count = count;
        PreviousButton.IsVisible = NextButton.IsVisible = count > 1;
        UpdateArrows();
    }

    private bool CanGoBack => !_busy && _index > 0;
    private bool CanGoForward => !_busy && _index < _count - 1;

    private void UpdateArrows()
    {
        PreviousButton.IsEnabled = CanGoBack;
        NextButton.IsEnabled = CanGoForward;
    }

    // The picture, which this now owns and disposes when it is replaced or
    // the viewer closes - always a decode of its own, never a bitmap the
    // album-art cache hands out to every view.
    public void ShowPicture(Bitmap picture, string caption, string? detail = null)
    {
        ReplacePicture(picture);
        Status.IsVisible = false;
        Spinner.IsVisible = false;
        Caption.Text = caption;
        Detail.Text = detail;
        Detail.IsVisible = !string.IsNullOrEmpty(detail);
        SetBusy(false);
    }

    // Words where the picture goes - "Downloading…" with the spinner
    // turning, or why there is none.
    public void ShowStatus(string status, bool busy = false)
    {
        ReplacePicture(null);
        Status.Text = status;
        Status.IsVisible = true;
        Spinner.IsVisible = busy;
        PictureDim.IsVisible = false;
        Caption.Text = "";
        Detail.IsVisible = false;
    }

    // The picture taken and being written - "Updating 12 files…" over it.
    public void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        UseButton.IsEnabled = !busy;
        UpdateArrows();
        PictureDim.IsVisible = busy && _picture is not null;
        Spinner.IsVisible = busy;
        Status.Text = status ?? "";
        Status.IsVisible = busy && status is not null;
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

        Dispatcher.UIThread.Post(() =>
        {
            Opacity = 1;
            // For the keys: a desktop's arrows step, Return takes the cover
            // and Escape closes, as a phone's hardware keyboard does too.
            Focus();
        });
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

    private void Previous_Click(object? sender, RoutedEventArgs e) => GoBack();

    private void Next_Click(object? sender, RoutedEventArgs e) => GoForward();

    private void GoBack()
    {
        if (CanGoBack)
            PreviousRequested?.Invoke(this, EventArgs.Empty);
    }

    private void GoForward()
    {
        if (CanGoForward)
            NextRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Viewer_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Left when OffersChoice:
                GoBack();
                break;
            case Key.Right when OffersChoice:
                GoForward();
                break;
            case Key.Enter when OffersChoice && UseButton.IsEnabled:
                Accepted?.Invoke(this, EventArgs.Empty);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Viewer_PointerPressed(object? sender, PointerPressedEventArgs e) =>
        _swipeStart = OffersChoice ? e.GetPosition(this) : null;

    // Right to left is the next cover, as a photo viewer turns.
    private void Viewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_swipeStart is not { } start)
            return;
        _swipeStart = null;

        var travel = e.GetPosition(this) - start;
        if (Math.Abs(travel.X) < SwipeDistance || Math.Abs(travel.X) < Math.Abs(travel.Y))
            return;

        if (travel.X < 0)
            GoForward();
        else
            GoBack();
    }
}
