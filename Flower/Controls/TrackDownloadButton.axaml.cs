using System;
using System.Windows.Input;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.ViewModels;

namespace Flower.Controls;

public partial class TrackDownloadButton : UserControl
{
    // How big the glyph itself is; IconPadding below is what turns that into
    // a tap target. 13 is what a desktop track row wants, which is
    // where this control started - a phone's row asks for 16 and its top bar
    // for 22.
    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<TrackDownloadButton, double>(nameof(IconSize), 13.0);

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    // The space around the glyph - the difference between an icon and
    // something a thumb can hit. The default keeps a 13px icon in the 16px
    // box a desktop track row has always given it.
    public static readonly StyledProperty<Thickness> IconPaddingProperty =
        AvaloniaProperty.Register<TrackDownloadButton, Thickness>(nameof(IconPadding), new Thickness(1.5));

    public Thickness IconPadding
    {
        get => GetValue(IconPaddingProperty);
        set => SetValue(IconPaddingProperty, value);
    }

    // The idle icon's tooltip - "Download" for one track, "Download all" for
    // an icon standing in for a whole screenful. The failed state's tooltip
    // is the same wherever it appears and stays in the XAML.
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<TrackDownloadButton, string>(nameof(Label), "Download");

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    // For the hosts that have a command to hand (mobile's row template and its
    // top bar, both inside XAML that has no code-behind of its own to hang an
    // event handler off). Everything else uses DownloadRequested below.
    //
    // Run from OnClick rather than bound onto the inner Button's own Command,
    // which looks equivalent and is not: Button.OnClick raises Click and only
    // then runs its Command, and only "if (!e.Handled)" - so this control's own
    // Click handler, which marks every click handled to keep it off the row
    // underneath, silently swallowed the command instead. That was a mobile
    // download button that lit up under the finger and did nothing at all.
    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<TrackDownloadButton, ICommand?>(nameof(Command));

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public static readonly StyledProperty<object?> CommandParameterProperty =
        AvaloniaProperty.Register<TrackDownloadButton, object?>(nameof(CommandParameter));

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public TrackDownloadButton()
    {
        InitializeComponent();
        DownloadButton.Click += OnClick;
    }

    // Raised instead of a bound ICommand: the DataContext here is whatever
    // view-model carries the icon's state (a track row, an expanded album's
    // song row, an album tile), none of which owns a command of its own - the
    // download runner lives on MainViewModel (see TrackDownloadRunner). Each
    // host decides what its own DataContext means: one track, or a whole
    // album's worth.
    public event EventHandler<DownloadIndicatorViewModel>? DownloadRequested;

    private void OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DownloadIndicatorViewModel indicator)
            DownloadRequested?.Invoke(this, indicator);

        // The two ways in are deliberately both live: a host either subscribes
        // to the event above or hands over a Command, and no host does both.
        // See Command's own remarks for why running it is this handler's job.
        if (Command is { } command && command.CanExecute(CommandParameter))
            command.Execute(CommandParameter);

        // Keeps the click from reaching the list's own pointer handling
        // underneath (MusicListPanel, AlbumGridView), which would otherwise
        // treat it as a row or tile click.
        e.Handled = true;
    }
}
