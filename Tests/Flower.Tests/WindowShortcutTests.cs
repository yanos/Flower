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

namespace Flower.Tests;

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
}
