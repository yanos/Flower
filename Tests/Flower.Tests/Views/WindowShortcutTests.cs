using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.Views;

using Xunit;

namespace Flower.Tests.Views;

// Space plays and pauses whichever control has focus - and when none does,
// which is where it once did nothing: with nothing focused Avalonia sends a
// key to the window itself, and the handler sat on MainView, under it. Except
// in a text box, where a space is a space.
[Collection("PlatformDataDirectory")]
public class WindowShortcutTests : PinnedDataDirectory
{
    public WindowShortcutTests() => TestIoc.EnsureConfigured();

    private static (Window Window, MainView View, MainViewModelHarness.Parts Parts) Paused()
    {
        var tracks = Enumerable.Range(1, 3).Select(i => new Track { Title = $"S{i}", Album = "Al", Artists = "Ar", Path = $"/m/{i}.mp3" }).ToList();
        var parts = MainViewModelHarness.BuildParts(new Library(tracks), new MainPlaylist(tracks));
        var view = new MainView { DataContext = parts.Main };
        var window = new Window { Width = 1000, Height = 700, Content = view };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        window.Show();
        parts.PlaylistControl.Play(tracks[0]);
        parts.Audio.IsPlaying = false;
        Dispatcher.UIThread.RunJobs();
        return (window, view, parts);
    }

    [AvaloniaFact]
    public void Space_resumes_with_nothing_focused()
    {
        var (window, _, parts) = Paused();
        using var _p = parts;
        window.Focus();

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, parts.Audio.ResumeCount);
        window.Close();
    }

    [AvaloniaFact]
    public void Space_in_a_text_box_types_a_space()
    {
        var (window, view, parts) = Paused();
        using var _p = parts;
        var filter = view.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
        filter.Focus();

        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, parts.Audio.ResumeCount);
        window.Close();
    }
    // Cmd on the Mac, Ctrl elsewhere - PlatformShortcuts.Primary.
    private static RawInputModifiers Primary =>
        System.OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public void Cmd_K_puts_the_keyboard_in_the_page_filter()
    {
        var (window, view, parts) = Paused();
        using var _p = parts;
        window.Focus();

        window.KeyPressQwerty(PhysicalKey.K, Primary);
        Dispatcher.UIThread.RunJobs();

        var focused = window.FocusManager?.GetFocusedElement() as Avalonia.Visual;
        Assert.NotNull(focused?.FindAncestorOfType<Flower.UserControls.FilterControl>(includeSelf: true));
        window.Close();
    }

    // With nothing selected where it was pressed - here Home - it opens the
    // song playing rather than nothing.
    [AvaloniaFact]
    public void Cmd_I_opens_track_info_with_nothing_selected()
    {
        var (window, _, parts) = Paused();
        using var _p = parts;
        parts.Main.SelectedSidebarItem = parts.Main.SidebarItems.First(i => i.Kind == Flower.ViewModels.SidebarItemKind.Home);
        Dispatcher.UIThread.RunJobs();
        window.Focus();

        window.KeyPressQwerty(PhysicalKey.I, Primary);
        Dispatcher.UIThread.RunJobs();

        var info = Assert.Single(window.OwnedWindows.OfType<TrackInfoWindow>());
        info.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Cmd_and_a_number_shows_that_library_row()
    {
        var (window, _, parts) = Paused();
        using var _p = parts;
        window.Focus();

        window.KeyPressQwerty(PhysicalKey.Digit2, Primary);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Flower.ViewModels.SidebarItemKind.Queue, parts.Main.SelectedSidebarItem?.Kind);

        window.KeyPressQwerty(PhysicalKey.Digit5, Primary);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(Flower.ViewModels.SidebarItemKind.Albums, parts.Main.SelectedSidebarItem?.Kind);
        window.Close();
    }
}
