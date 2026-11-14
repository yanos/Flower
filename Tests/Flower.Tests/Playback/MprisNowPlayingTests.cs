using System.Collections.Concurrent;

using Tmds.DBus.Protocol;

using Flower.Platform;
using Flower.Services;

namespace Flower.Tests.Playback;

// Linux's media keys and media widget, driven the way a desktop drives them:
// another client on the bus calling MprisNowPlaying's methods and reading its
// properties. A private dbus-daemon (PrivateSessionBus) stands in for the
// session bus, so this runs wherever dbus-daemon is installed - CI's Linux
// runner, and a Mac with MacPorts' or Homebrew's.
public sealed class MprisNowPlayingTests : IAsyncLifetime
{
    private const string BusName = MprisNowPlaying.DefaultBusName;
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";

    private PrivateSessionBus? _bus;
    private MprisNowPlaying? _player;
    private DBusConnection? _desktop;
    private readonly string _artDirectory = Directory.CreateTempSubdirectory("flower-mpris-art").FullName;
    private readonly ConcurrentQueue<NowPlayingCommand> _commands = new();

    public async ValueTask InitializeAsync()
    {
        if (PrivateSessionBus.FindDaemon() is not { } daemon)
            return;

        _bus = PrivateSessionBus.Start(daemon);
        _player = new MprisNowPlaying(_bus.Address, artDirectory: _artDirectory);
        _player.CommandReceived += (_, command) => _commands.Enqueue(command);
        await _player.Ready;

        _desktop = new DBusConnection(_bus.Address);
        await _desktop.ConnectAsync();
    }

    public ValueTask DisposeAsync()
    {
        _desktop?.Dispose();
        _player?.Dispose();
        _bus?.Dispose();
        Directory.Delete(_artDirectory, recursive: true);
        return default;
    }

    private DBusConnection Desktop()
    {
        Assert.SkipWhen(_desktop == null, "Needs dbus-daemon to stand in for the session bus.");
        return _desktop!;
    }

    private static NowPlayingMetadata Song(string title = "Hey Jude", byte[]? art = null) => new()
    {
        Title = title,
        Artist = "The Beatles",
        Album = "1",
        Duration = TimeSpan.FromSeconds(431),
        ArtworkData = art
    };

    private static Task CallAsync(DBusConnection desktop, string member)
    {
        using var writer = desktop.GetMessageWriter();
        writer.WriteMethodCallHeader(BusName, ObjectPath, PlayerInterface, member, null, MessageFlags.None);
        return desktop.CallMethodAsync(writer.CreateMessage());
    }

    private static Task<VariantValue> GetAsync(DBusConnection desktop, string property)
    {
        using var writer = desktop.GetMessageWriter();
        writer.WriteMethodCallHeader(BusName, ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
        writer.WriteString(PlayerInterface);
        writer.WriteString(property);
        return desktop.CallMethodAsync(writer.CreateMessage(), (message, _) => message.GetBodyReader().ReadVariantValue(), null);
    }

    [Fact]
    public async Task The_desktops_play_pause_next_and_previous_reach_Flower()
    {
        var desktop = Desktop();
        _player!.UpdateMetadata(Song());
        _player.UpdatePlaybackState(isPlaying: true, TimeSpan.Zero);

        await CallAsync(desktop, "PlayPause");
        await CallAsync(desktop, "Next");
        await CallAsync(desktop, "Previous");

        Assert.Equal([NowPlayingCommand.PlayPause, NowPlayingCommand.Next, NowPlayingCommand.Previous], _commands);
    }

    // Flower has one toggle; MPRIS has Play, Pause and Stop as well. Each must
    // only ever move playback the way it says, or a Pause pressed while
    // already paused would start the music.
    [Fact]
    public async Task Play_only_resumes_and_pause_and_stop_only_pause()
    {
        var desktop = Desktop();
        _player!.UpdateMetadata(Song());

        _player.UpdatePlaybackState(isPlaying: true, TimeSpan.Zero);
        await CallAsync(desktop, "Play");
        await CallAsync(desktop, "Pause");
        Assert.Equal([NowPlayingCommand.PlayPause], _commands);

        _commands.Clear();
        _player.UpdatePlaybackState(isPlaying: false, TimeSpan.Zero);
        await CallAsync(desktop, "Pause");
        await CallAsync(desktop, "Stop");
        await CallAsync(desktop, "Play");
        Assert.Equal([NowPlayingCommand.PlayPause], _commands);
    }

    [Fact]
    public async Task The_desktop_reads_the_song_playing_and_whether_it_plays()
    {
        var desktop = Desktop();
        byte[] cover = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        _player!.UpdateMetadata(Song(art: cover));
        _player.UpdatePlaybackState(isPlaying: true, TimeSpan.FromSeconds(12));

        var metadata = (await GetAsync(desktop, "Metadata")).GetDictionary<string, VariantValue>();
        Assert.Equal("Hey Jude", metadata["xesam:title"].GetString());
        Assert.Equal(["The Beatles"], metadata["xesam:artist"].GetArray<string>());
        Assert.Equal("1", metadata["xesam:album"].GetString());
        Assert.Equal(431_000_000, metadata["mpris:length"].GetInt64());
        var art = new Uri(metadata["mpris:artUrl"].GetString());
        Assert.Equal(cover, File.ReadAllBytes(art.LocalPath));

        Assert.Equal("Playing", (await GetAsync(desktop, "PlaybackStatus")).GetString());
        Assert.InRange((await GetAsync(desktop, "Position")).GetInt64(), 12_000_000, 20_000_000);

        _player.UpdatePlaybackState(isPlaying: false, TimeSpan.FromSeconds(12));
        Assert.Equal("Paused", (await GetAsync(desktop, "PlaybackStatus")).GetString());

        _player.Clear();
        Assert.Equal("Stopped", (await GetAsync(desktop, "PlaybackStatus")).GetString());
        Assert.False(File.Exists(art.LocalPath));
    }

    // A media widget does not poll: it redraws on PropertiesChanged, so a
    // pause that is not announced leaves it showing a pause button.
    [Fact]
    public async Task A_pause_is_announced_to_the_desktop()
    {
        var desktop = Desktop();
        _player!.UpdateMetadata(Song());
        _player.UpdatePlaybackState(isPlaying: true, TimeSpan.Zero);

        var announced = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await desktop.WatchSignalAsync(
            null, ObjectPath, "org.freedesktop.DBus.Properties", "PropertiesChanged",
            (message, _) =>
            {
                var reader = message.GetBodyReader();
                reader.ReadString();
                return reader.ReadDictionaryOfStringToVariantValue();
            },
            notification =>
            {
                if (notification.HasValue && notification.Value.TryGetValue("PlaybackStatus", out var status))
                    announced.TrySetResult(status.GetString());
            },
            ObserverFlags.None, false, null);

        _player.UpdatePlaybackState(isPlaying: false, TimeSpan.FromSeconds(3));

        Assert.Equal("Paused", await announced.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
