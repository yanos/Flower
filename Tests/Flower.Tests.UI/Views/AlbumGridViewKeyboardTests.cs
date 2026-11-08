using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;

using Flower.Controls;
using Flower.Models;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Views;

// The keys reach the Albums grid once it has the focus, and come out as the
// two things MainView acts on: a tile moved to, and a tile activated. Where an
// arrow goes is AlbumGridKeyboard's (AlbumGridKeyboardTests).
public class AlbumGridViewKeyboardTests
{
    public AlbumGridViewKeyboardTests() => TestIoc.EnsureConfigured();

    private static (Window Window, AlbumGridView Grid, List<AlbumTileViewModel> Tiles) Show()
    {
        var tiles = Enumerable.Range(0, 6).Select(i => new AlbumTileViewModel
        {
            Name = $"Album {i}",
            RepresentativeTrack = new Track { Path = $"/music/{i}.mp3", Album = $"Album {i}" },
            Tracks = [],
        }).ToList();
        var grid = new AlbumGridView();
        var window = new Window { Width = 900, Height = 600, Content = grid };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        grid.ItemsSource = new ObservableCollection<AlbumTileViewModel>(tiles);
        grid.SelectedNames = new[] { "Album 1" };
        grid.Focus();
        return (window, grid, tiles);
    }

    [AvaloniaFact]
    public void An_arrow_moves_from_the_selected_tile()
    {
        var (window, grid, tiles) = Show();
        var moved = new List<AlbumTileViewModel>();
        grid.TileMovedTo += (_, tile) => moved.Add(tile);

        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);

        Assert.Same(tiles[2], Assert.Single(moved));
    }

    [AvaloniaFact]
    public void Enter_activates_the_selected_tile()
    {
        var (window, grid, tiles) = Show();
        AlbumTileViewModel? activated = null;
        grid.TileActivated += (_, tile) => activated = tile;

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Same(tiles[1], activated);
    }

    // Alt+Left is Back off macOS (MainView_PreviewKeyDown), not a move.
    [AvaloniaFact]
    public void A_modified_arrow_is_left_to_the_shortcuts()
    {
        var (window, grid, _) = Show();
        var moved = 0;
        grid.TileMovedTo += (_, _) => moved++;

        window.KeyPressQwerty(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);

        Assert.Equal(0, moved);
    }
}
