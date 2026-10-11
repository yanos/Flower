using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Flower.Controls;
using Flower.Importer;
using Flower.Models;
using Flower.Persistence;
using Flower.Persistence.Sql;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.Persistence;

// settings.json, and the AtomicJsonFile machinery under every JSON store.
//
// Before AtomicJsonFile, every store wrote straight over its live file, so a
// crash mid-write truncated it - and every Load() catches a bad parse and
// returns "empty", silently discarding the user's state. These pin the
// recovery. Driven through AppSettingsStore because the library moved to
// SQLite and no longer goes through AtomicJsonFile at all, but settings.json,
// device.json, trusted-peers.json and the rest still do.
[Collection("PlatformDataDirectory")]
public class AppSettingsStoreTests : IsolatedHome
{
    [Fact]
    public async Task AppSettingsStore_round_trips_window_geometry()
    {
        var settings = new AppSettings
        {
            WindowWidth       = 1024,
            WindowHeight      = 768,
            WindowX           = 50,
            WindowY           = 60,
            WindowIsMaximized = true,
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.Equal(1024, loaded.WindowWidth);
        Assert.Equal(768,  loaded.WindowHeight);
        Assert.Equal(50,   loaded.WindowX);
        Assert.Equal(60,   loaded.WindowY);
        Assert.True(loaded.WindowIsMaximized);
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_repeat_and_shuffle_toggles()
    {
        var settings = new AppSettings { IsRepeatEnabled = true, IsShuffleEnabled = true };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.True(loaded.IsRepeatEnabled);
        Assert.True(loaded.IsShuffleEnabled);
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_last_view_state()
    {
        var settings = new AppSettings
        {
            LastSidebarKind   = "Playlist",
            LastPlaylistName  = "Favorites",
            LastScrollOffsetY = 1234.5,
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.Equal("Playlist",  loaded.LastSidebarKind);
        Assert.Equal("Favorites", loaded.LastPlaylistName);
        Assert.Equal(1234.5,      loaded.LastScrollOffsetY);
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_the_paired_server()
    {
        var settings = new AppSettings
        {
            PairedServerFingerprint  = "abc123",
            PairedServerAlias        = "Living Room Mac",
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.Equal("abc123",          loaded.PairedServerFingerprint);
        Assert.Equal("Living Room Mac", loaded.PairedServerAlias);
    }

    [Fact]
    public void AppSettingsStore_Save_is_synchronous_and_round_trips_window_geometry()
    {
        var settings = new AppSettings { WindowWidth = 900, WindowHeight = 600 };

        new AppSettingsStore().Save(settings);
        var loaded = new AppSettingsStore().Load();

        Assert.Equal(900, loaded.WindowWidth);
        Assert.Equal(600, loaded.WindowHeight);
    }

    [Fact]
    public async Task AppSettingsStore_round_trips_column_states()
    {
        var settings = new AppSettings
        {
            ColumnStates = new List<ColumnState>
            {
                new() { Id = "Title", IsVisible = true, Width = 197.5, Order = 0 },
                new() { Id = "Artist", IsVisible = false, Width = 150, Order = 1 },
            },
        };

        await new AppSettingsStore().SaveAsync(settings);
        var loaded = new AppSettingsStore().Load().ColumnStates;

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        var title = loaded.Single(s => s.Id == "Title");
        Assert.Equal(197.5, title.Width);
        Assert.True(title.IsVisible);
        var artist = loaded.Single(s => s.Id == "Artist");
        Assert.False(artist.IsVisible);
    }

    [Fact]
    public async Task AppSettingsStore_recovers_from_the_backup_when_the_live_file_is_truncated()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var settings = new AppSettings { SortColumn = "Album", SortAscending = false };

        // Two saves: the first creates the file, the second rotates it into .bak.
        await store.SaveAsync(settings);
        await store.SaveAsync(settings);

        Truncate(AppSettingsStore.StorePath);

        var loaded = store.Load();

        Assert.Equal("Album", loaded.SortColumn);
        Assert.False(loaded.SortAscending);
    }

    // The recovered contents go back to the live path, so a user who quits
    // before the next save still keeps them.
    [Fact]
    public async Task AppSettingsStore_writes_the_recovered_contents_back_to_the_live_file()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var settings = new AppSettings { SortColumn = "Year" };

        await store.SaveAsync(settings);
        await store.SaveAsync(settings);
        Truncate(AppSettingsStore.StorePath);

        store.Load();

        // A second, independent store instance reads the live file with no
        // recovery step of its own.
        var reloaded = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance).Load();
        Assert.Equal("Year", reloaded.SortColumn);
    }

    // With no backup to fall back on the data really is gone, but the bad file
    // is preserved for a bug report rather than being silently overwritten by
    // the next save.
    [Fact]
    public async Task AppSettingsStore_quarantines_an_unreadable_file_when_there_is_no_backup()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        await store.SaveAsync(new AppSettings { SortColumn = "Album" });
        Truncate(AppSettingsStore.StorePath);

        var loaded = store.Load();

        Assert.Null(loaded.SortColumn);
        Assert.True(File.Exists(AtomicJsonFile.CorruptPath(AppSettingsStore.StorePath)));
    }

    // AppSettingsStore was the one store with no write lock, while
    // ColumnManager's debounced save fires on every pixel of a column-resize
    // drag - overlapping writes were routine, and on Windows they threw.
    [Fact]
    public async Task AppSettingsStore_survives_many_concurrent_saves()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var settings = new AppSettings { SortColumn = "Album", SortAscending = false };

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => store.SaveAsync(settings)));

        var reloaded = store.Load();
        Assert.Equal("Album", reloaded.SortColumn);
        Assert.False(reloaded.SortAscending);
    }

    [Fact]
    public void ColumnManager_Flush_synchronously_persists_widths_for_the_next_launch()
    {
        // Simulates a resize followed immediately by quitting: Flush() must land
        // on disk without waiting for the normal 500ms debounce (see
        // ColumnManager.ScheduleSave), which the process might not survive long
        // enough to complete.
        var appSettingsStore = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        var first = new ColumnManager(new AppSettings(), appSettingsStore);
        var title = first.Columns.Single(c => c.Id == "Title");
        title.Width = 321;
        first.Flush();

        // A brand-new ColumnManager reading the just-persisted settings.json
        // simulates the next app launch.
        var second = new ColumnManager(appSettingsStore.Load(), appSettingsStore);
        Assert.Equal(321, second.Columns.Single(c => c.Id == "Title").Width);
    }

    [Fact]
    public async Task AtomicJsonFile_leaves_no_temp_file_behind_after_a_successful_write()
    {
        var store = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance);
        await store.SaveAsync(new AppSettings { SortColumn = "Album" });

        Assert.False(File.Exists(AppSettingsStore.StorePath + ".tmp"));
        Assert.True(File.Exists(AppSettingsStore.StorePath));
    }

    // The test above only proves one store instance serializes its own saves.
    // The lock that does that is per-instance while the file is per-path, so
    // two stores over the same path - two DI containers in one process, or any
    // store built through its parameterless constructor rather than resolved -
    // serialized against nothing and collided on the shared fixed .tmp name.
    // That is what made CompositionRootTests fail every second or third run
    // with "the process cannot access settings.json.tmp because it is being
    // used by another process"; the lock now lives in AtomicJsonFile, keyed on
    // the path.
    [Fact]
    public async Task Concurrent_saves_from_separate_store_instances_do_not_collide()
    {
        var stores = Enumerable.Range(0, 8)
            .Select(_ => new AppSettingsStore(NullLogger<AppSettingsStore>.Instance))
            .ToList();
        var settings = new AppSettings { SortColumn = "Album", SortAscending = false };

        // Sync Save and async SaveAsync interleaved deliberately - they are
        // different code paths into the same file, and MainWindow.Closing fires
        // the synchronous one exactly while debounced async saves are in flight.
        await Task.WhenAll(stores.SelectMany(store => new[]
        {
            store.SaveAsync(settings),
            Task.Run(() => store.Save(settings)),
        }));

        Assert.False(File.Exists(AppSettingsStore.StorePath + ".tmp"));
        var reloaded = new AppSettingsStore(NullLogger<AppSettingsStore>.Instance).Load();
        Assert.Equal("Album", reloaded.SortColumn);
    }

    // Half a file is the realistic crash shape: valid JSON prefix, no closing
    // bracket. An empty file would also fail to parse, but wouldn't prove the
    // recovery path handles partial content.
    private static void Truncate(string path)
    {
        var contents = File.ReadAllText(path);
        File.WriteAllText(path, contents[..(contents.Length / 2)]);
    }
}
