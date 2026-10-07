using System;
using System.Collections.Generic;
using System.ComponentModel;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Material.Icons;
using Material.Icons.Avalonia;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Services;
using Flower.ViewModels.Mobile;

namespace Flower.Controls;

// See ScreenMenuButton.axaml. Settings is always the menu's last entry; what
// sits above it is the host's - a screen's (MobileMainViewModel.BuildScreenMenu)
// or the Now Playing sheet's (BuildNowPlayingMenu) - so the entries are built
// here as the menu opens rather than bound.
public partial class ScreenMenuButton : UserControl
{
    /// <summary>
    /// What the menu holds, asked on every opening, for the screen as it is
    /// right then: which entries a screen has, which sort is ticked and whether
    /// there is anything left to download all change between openings.
    /// </summary>
    public Func<IReadOnlyList<ScreenMenuSection>>? Sections { get; set; }

    public ScreenMenuButton()
    {
        InitializeComponent();
    }

    private void ScreenMenu_Opening(object? sender, EventArgs e)
    {
        ScreenMenuItems.Children.Clear();
        if (Sections?.Invoke() is not { } sections)
            return;

        var first = true;
        foreach (var section in sections)
        {
            if (!first)
            {
                ScreenMenuItems.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(0, 4),
                    [!Border.BackgroundProperty] = new DynamicResourceExtension("AppPrimaryBorderBrush"),
                });
            }
            first = false;

            if (section.Header is { } header)
            {
                ScreenMenuItems.Children.Add(new TextBlock
                {
                    Text = header,
                    FontSize = 12,
                    FontWeight = FontWeight.SemiBold,
                    Opacity = 0.5,
                    Margin = new Thickness(16, 8, 16, 2),
                });
            }

            foreach (var entry in section.Entries)
                ScreenMenuItems.Children.Add(MenuRow(entry));
        }
    }

    // Opening and closing, the card grows out of the button's corner and
    // folds back into it - from there because that is where the menu comes
    // from. Quick: it is in the way of the tap that follows.
    // Built by XAML, so there is no constructor to inject one through.
    private static readonly ILogger Logger = AppLogging.CreateLogger(typeof(ScreenMenuButton).FullName!);

    private static readonly TimeSpan MenuOpenDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan MenuCloseDuration = TimeSpan.FromMilliseconds(120);
    private const double MenuFoldedScale = 0.6;

    // Set while the closing animation runs, and while the Hide that follows
    // it goes through - so that Hide is let past, and a second close asked
    // for mid-animation (a tap on the backdrop, then another) starts nothing.
    private bool _isMenuClosing;

    private FlyoutPresenter? MenuCard => ScreenMenuItems.FindAncestorOfType<FlyoutPresenter>();

    private void ScreenMenu_Opened(object? sender, EventArgs e)
    {
        if (MenuCard is not { } card)
            return;
        var scale = new ScaleTransform(MenuFoldedScale, MenuFoldedScale);
        card.RenderTransformOrigin = new RelativePoint(1, 0, RelativeUnit.Relative);
        card.RenderTransform = scale;
        // Folded before the first frame is drawn, not on the animation's first
        // tick, which can land after it - a menu that flashed open at full
        // size and then shrank to grow again.
        card.Opacity = 0;
        MenuAnimation(MenuOpenDuration, new CubicEaseOut(), from: (MenuFoldedScale, 0), to: (1, 1))
            .RunAsync(card).Forget(Logger, "Screen menu opening");
    }

    private async void ScreenMenu_Closing(object? sender, CancelEventArgs e)
    {
        if (_isMenuClosing)
            return;
        if (MenuCard is not { } card)
            return;

        e.Cancel = true;
        _isMenuClosing = true;
        try
        {
            await MenuAnimation(MenuCloseDuration, new CubicEaseIn(), from: (1, 1), to: (MenuFoldedScale, 0))
                .RunAsync(card);
            MenuButton.Flyout?.Hide();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Screen menu closing animation failed");
            MenuButton.Flyout?.Hide();
        }
        finally
        {
            _isMenuClosing = false;
        }
    }

    private static Animation MenuAnimation(TimeSpan duration, Easing easing,
        (double Scale, double Opacity) from, (double Scale, double Opacity) to) => new()
    {
        Duration = duration,
        Easing = easing,
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame
            {
                Cue = new Cue(0),
                Setters =
                {
                    new Setter(ScaleTransform.ScaleXProperty, from.Scale),
                    new Setter(ScaleTransform.ScaleYProperty, from.Scale),
                    new Setter(OpacityProperty, from.Opacity),
                },
            },
            new KeyFrame
            {
                Cue = new Cue(1),
                Setters =
                {
                    new Setter(ScaleTransform.ScaleXProperty, to.Scale),
                    new Setter(ScaleTransform.ScaleYProperty, to.Scale),
                    new Setter(OpacityProperty, to.Opacity),
                },
            },
        },
    };

    private Button MenuRow(ScreenMenuEntry entry)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14 };
        var icon = new MaterialIcon { Kind = entry.Icon, Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        var label = new TextBlock { Text = entry.Label, FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        // Red, like the same entries on the song and album menus.
        if (entry.IsDestructive)
        {
            icon[!MaterialIcon.ForegroundProperty] = new DynamicResourceExtension("AppErrorBrush");
            label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("AppErrorBrush");
        }
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        // The sort in use says which way it runs; picking it again turns it
        // round (MobileMainViewModel.ChooseSort).
        if (entry.Direction is { } direction)
        {
            var arrow = new MaterialIcon
            {
                Kind = direction == ListSortDirection.Ascending ? MaterialIconKind.ArrowUp : MaterialIconKind.ArrowDown,
                Width = 20,
                Height = 20,
                VerticalAlignment = VerticalAlignment.Center,
                [!MaterialIcon.ForegroundProperty] = new DynamicResourceExtension("AppAccentBrush"),
            };
            Grid.SetColumn(arrow, 2);
            row.Children.Add(arrow);
        }

        var button = new Button { Content = row, Classes = { "screenMenuRow" } };
        // Closed first, so a sheet the entry raises (Settings, Add to
        // Playlist) comes up over the screen rather than under the menu.
        button.Click += (_, _) =>
        {
            MenuButton.Flyout?.Hide();
            entry.Invoke();
        };
        return button;
    }
}
