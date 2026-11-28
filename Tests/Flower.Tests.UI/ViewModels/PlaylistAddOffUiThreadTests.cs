using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Flower.Models;
using Flower.Tests.TestSupport;

using Xunit;

namespace Flower.Tests.ViewModels;

// UF-16: adding songs to a playlist saves every playlist, and a whole library
// added from the UI thread froze the app while it did. The edit and its save
// happen off it; the page still shows the songs once the add is done.
[Collection("PlatformDataDirectory")]
public class PlaylistAddOffUiThreadTests : PinnedDataDirectory
{
    private sealed class ThreadNotingStore : IPlaylistStore
    {
        public readonly List<bool> OnUiThread = [];

        public void Save(IEnumerable<Playlist> playlists)
        {
            lock (OnUiThread)
                OnUiThread.Add(Dispatcher.UIThread.CheckAccess());
        }
    }

    [AvaloniaFact]
    public async Task Adding_songs_saves_off_the_ui_thread_and_then_shows_them()
    {
        var songs = Enumerable.Range(0, 2000).Select(i => new Track { Title = $"Song {i}", Path = $"/music/{i}.mp3" }).ToList();
        var store = new ThreadNotingStore();
        var library = new Library(songs, NullLogger<Library>.Instance, playlistStore: store);
        var playlist = new Playlist("Everything", []);
        library.AddPlaylist(playlist);
        using var parts = MainViewModelHarness.Build(library, new MainPlaylist([]));
        parts.Main.SelectedSidebarItem = parts.Main.SidebarItems.Single(i => i.Playlist?.Id == playlist.Id);
        lock (store.OnUiThread)
            store.OnUiThread.Clear();

        await parts.Main.AddTracksToPlaylist(songs, playlist);

        Assert.Equal([false], store.OnUiThread);
        Assert.Equal(2000, playlist.Tracks.Count);
        // The page's rows are rebuilt in the background too. The count is in
        // the current culture's digits, as the page writes it.
        await UiWait.Until(() => parts.Main.PageSubtitle.Contains($"{2000:N0} songs"), "the page shows the songs added");
    }
}
