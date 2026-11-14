using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Tmds.DBus.Protocol;

using Flower.Logging;
using Flower.Services;

namespace Flower.Platform;

// Linux's half of docs/MEDIA-KEYS-PLAN.md: Flower as an MPRIS player on the
// session bus. The desktop owns the media keys and forwards them to the
// player it thinks is current, and its media widget (GNOME's in the clock
// menu, KDE's in the tray, playerctl) shows what this publishes - so being a
// well-behaved MPRIS player is the whole feature, there is no key capture.
//
// Hand-written over Tmds.DBus.Protocol, which Avalonia's FreeDesktop backend
// already ships: the object answers org.mpris.MediaPlayer2, .Player, the
// standard Properties interface and introspection itself. The seam
// (IPlatformNowPlaying) carries play/pause, next and previous only, so the
// player says CanSeek false, and Play, Pause and Stop map onto the one toggle
// by the state it last published.
//
// Plain net10.0 in the shared library rather than in Flower.Desktop, so the
// suite can run it against a private bus (MprisNowPlayingTests); only
// Flower.Desktop constructs it, and only on Linux.
public sealed class MprisNowPlaying : IPlatformNowPlaying, IPathMethodHandler, IDisposable
{
    public const string DefaultBusName = "org.mpris.MediaPlayer2.flower";

    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    // A position that has drifted this far from where the last update
    // predicted is a seek, which MPRIS clients learn of only by the Seeked
    // signal: Position is never sent in PropertiesChanged.
    private static readonly TimeSpan SeekThreshold = TimeSpan.FromSeconds(1.5);

    private readonly object _gate = new();
    private readonly DBusConnection _connection;
    private readonly string _artDirectory;
    private ILogger? _logger;

    private bool _connected;
    private bool _hasTrack;
    private bool _isPlaying;
    private NowPlayingMetadata? _metadata;
    private long _trackSerial;
    private string? _artPath;
    private TimeSpan _elapsed;
    private long _elapsedStamp;

    public event EventHandler<NowPlayingCommand>? CommandReceived;

    // The bus name actually owned: DefaultBusName, or that with
    // ".instance<pid>" (the spec's convention) when another Flower holds it.
    public string? OwnedBusName { get; private set; }

    // Finishes once the object is on the bus and its name is owned, or once
    // that has failed and been logged - never faults.
    public Task Ready { get; }

    // Null where there is no session bus (DBUS_SESSION_BUS_ADDRESS unset: a
    // console login, a container), in which case Flower has no media keys on
    // Linux and nothing else changes.
    public static MprisNowPlaying? ForSessionBus() =>
        DBusAddress.Session is { Length: > 0 } address ? new MprisNowPlaying(address) : null;

    public MprisNowPlaying(string busAddress, string busName = DefaultBusName, string? artDirectory = null)
    {
        _connection = new DBusConnection(busAddress);
        _artDirectory = artDirectory ?? DefaultArtDirectory();
        Ready = ConnectAsync(busName);
    }

    private ILogger Logger => _logger ??= AppLogging.CreateLogger<MprisNowPlaying>();

    private async Task ConnectAsync(string busName)
    {
        try
        {
            await _connection.ConnectAsync();
            _connection.AddMethodHandler(this);

            var name = busName;
            if (!await _connection.TryRequestNameAsync(name, RequestNameOptions.None))
            {
                name = $"{busName}.instance{Environment.ProcessId}";
                await _connection.RequestNameAsync(name, RequestNameOptions.None);
            }

            lock (_gate)
            {
                OwnedBusName = name;
                _connected = true;
            }
            Logger.LogInformation("MPRIS player on the session bus as {BusName}", name);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Could not put Flower on the session bus; media keys and the desktop's media controls will not reach it");
        }
    }

    public void UpdateMetadata(NowPlayingMetadata metadata)
    {
        string? previousArt;
        string? art;
        lock (_gate)
        {
            _metadata = metadata;
            _hasTrack = true;
            _trackSerial++;
            // The update that follows a new song says where it is; until
            // then it is at its start, not where the last song had got to.
            _elapsed = TimeSpan.Zero;
            _elapsedStamp = Stopwatch.GetTimestamp();
            previousArt = _artPath;
            art = _artPath = WriteArt(metadata.ArtworkData);
        }
        if (previousArt != null && previousArt != art)
            TryDelete(previousArt);

        EmitPlayerChanged(includeMetadata: true);
    }

    public void UpdatePlaybackState(bool isPlaying, TimeSpan elapsed)
    {
        bool statusChanged;
        bool seeked;
        lock (_gate)
        {
            seeked = _hasTrack && (elapsed - PredictedPosition()).Duration() > SeekThreshold;
            statusChanged = _isPlaying != isPlaying;
            _isPlaying = isPlaying;
            _elapsed = elapsed;
            _elapsedStamp = Stopwatch.GetTimestamp();
        }

        if (statusChanged)
            EmitPlayerChanged(includeMetadata: false);
        if (seeked)
            EmitSeeked(elapsed);
    }

    public void Clear()
    {
        string? art;
        lock (_gate)
        {
            _hasTrack = false;
            _isPlaying = false;
            _metadata = null;
            _elapsed = TimeSpan.Zero;
            art = _artPath;
            _artPath = null;
        }
        if (art != null)
            TryDelete(art);

        EmitPlayerChanged(includeMetadata: true);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (_artPath != null)
            TryDelete(_artPath);
    }

    // ---- D-Bus side -------------------------------------------------------

    string IPathMethodHandler.Path => ObjectPath;

    bool IPathMethodHandler.HandlesChildPaths => false;

    ValueTask IPathMethodHandler.HandleMethodAsync(MethodContext context)
    {
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([IntrospectionXml], []);
            return default;
        }

        var request = context.Request;
        var member = request.MemberAsString;
        switch (request.InterfaceAsString)
        {
            case PropertiesInterface:
                HandleProperties(context, member);
                break;
            case RootInterface:
                // Raise and Quit: CanRaise and CanQuit say neither is offered.
                if (member is "Raise" or "Quit")
                    ReplyEmpty(context);
                else
                    context.ReplyUnknownMethodError();
                break;
            case PlayerInterface:
            case null:
                HandlePlayer(context, member);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
        return default;
    }

    private void HandlePlayer(MethodContext context, string? member)
    {
        bool isPlaying;
        lock (_gate)
            isPlaying = _isPlaying;

        switch (member)
        {
            case "PlayPause":
                Raise(NowPlayingCommand.PlayPause);
                break;
            case "Play":
                if (!isPlaying)
                    Raise(NowPlayingCommand.PlayPause);
                break;
            case "Pause":
            case "Stop":
                if (isPlaying)
                    Raise(NowPlayingCommand.PlayPause);
                break;
            case "Next":
                Raise(NowPlayingCommand.Next);
                break;
            case "Previous":
                Raise(NowPlayingCommand.Previous);
                break;
            case "Seek":
            case "SetPosition":
                // CanSeek is false; the spec has the player ignore these.
                break;
            case "OpenUri":
                context.ReplyError("org.mpris.MediaPlayer2.Player.Error.NotSupported", "Flower does not open URIs over MPRIS.");
                return;
            default:
                context.ReplyUnknownMethodError();
                return;
        }
        ReplyEmpty(context);
    }

    private void HandleProperties(MethodContext context, string? member)
    {
        var reader = context.Request.GetBodyReader();
        switch (member)
        {
            case "Get":
            {
                var iface = reader.ReadString();
                var name = reader.ReadString();
                if (!PropertiesOf(iface).TryGetValue(name, out var value))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {name} on {iface}.");
                    return;
                }
                using var writer = context.CreateReplyWriter("v");
                writer.WriteVariant(value);
                context.Reply(writer.CreateMessage());
                return;
            }
            case "GetAll":
            {
                var iface = reader.ReadString();
                using var writer = context.CreateReplyWriter("a{sv}");
                writer.WriteDictionary(PropertiesOf(iface));
                context.Reply(writer.CreateMessage());
                return;
            }
            case "Set":
                context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", "Flower's MPRIS properties are read-only.");
                return;
            default:
                context.ReplyUnknownMethodError();
                return;
        }
    }

    private Dictionary<string, VariantValue> PropertiesOf(string iface) => iface switch
    {
        RootInterface => RootProperties(),
        PlayerInterface => PlayerProperties(includePosition: true),
        _ => new Dictionary<string, VariantValue>()
    };

    private static Dictionary<string, VariantValue> RootProperties() => new()
    {
        ["CanQuit"] = false,
        ["CanRaise"] = false,
        ["HasTrackList"] = false,
        ["Identity"] = "Flower",
        ["SupportedUriSchemes"] = VariantValue.Array(Array.Empty<string>()),
        ["SupportedMimeTypes"] = VariantValue.Array(Array.Empty<string>())
    };

    private Dictionary<string, VariantValue> PlayerProperties(bool includePosition)
    {
        lock (_gate)
        {
            var properties = new Dictionary<string, VariantValue>
            {
                ["PlaybackStatus"] = !_hasTrack ? "Stopped" : _isPlaying ? "Playing" : "Paused",
                ["Metadata"] = MetadataValue(),
                ["Rate"] = 1.0,
                ["MinimumRate"] = 1.0,
                ["MaximumRate"] = 1.0,
                ["Volume"] = 1.0,
                ["CanGoNext"] = _hasTrack,
                ["CanGoPrevious"] = _hasTrack,
                ["CanPlay"] = _hasTrack,
                ["CanPause"] = _hasTrack,
                ["CanSeek"] = false,
                ["CanControl"] = true
            };
            if (includePosition)
                properties["Position"] = Microseconds(PredictedPosition());
            return properties;
        }
    }

    // Under _gate.
    private VariantValue MetadataValue()
    {
        var metadata = new Dict<string, VariantValue>();
        if (_metadata is not { } track)
            return metadata;

        metadata.Add("mpris:trackid", new ObjectPath($"/org/flower/track/{_trackSerial}"));
        if (track.Duration > TimeSpan.Zero)
            metadata.Add("mpris:length", Microseconds(track.Duration));
        if (!string.IsNullOrEmpty(track.Title))
            metadata.Add("xesam:title", track.Title);
        if (!string.IsNullOrEmpty(track.Artist))
            metadata.Add("xesam:artist", VariantValue.Array(new[] { track.Artist }));
        if (!string.IsNullOrEmpty(track.Album))
            metadata.Add("xesam:album", track.Album);
        if (_artPath != null)
            metadata.Add("mpris:artUrl", new Uri(_artPath).AbsoluteUri);
        return metadata;
    }

    // Under _gate.
    private TimeSpan PredictedPosition() =>
        _isPlaying ? _elapsed + Stopwatch.GetElapsedTime(_elapsedStamp) : _elapsed;

    private static long Microseconds(TimeSpan span) => span.Ticks / (TimeSpan.TicksPerMillisecond / 1000);

    private void Raise(NowPlayingCommand command) => CommandReceived?.Invoke(this, command);

    private static void ReplyEmpty(MethodContext context)
    {
        if (context.NoReplyExpected)
            return;
        using var writer = context.CreateReplyWriter(null!);
        context.Reply(writer.CreateMessage());
    }

    private void EmitPlayerChanged(bool includeMetadata)
    {
        lock (_gate)
        {
            if (!_connected)
                return;
        }

        var changed = PlayerProperties(includePosition: false);
        if (!includeMetadata)
            changed.Remove("Metadata");

        using var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(path: ObjectPath, @interface: PropertiesInterface, member: "PropertiesChanged", signature: "sa{sv}as");
        writer.WriteString(PlayerInterface);
        writer.WriteDictionary(changed);
        writer.WriteArray(Array.Empty<string>());
        _connection.TrySendMessage(writer.CreateMessage());
    }

    private void EmitSeeked(TimeSpan position)
    {
        lock (_gate)
        {
            if (!_connected)
                return;
        }

        using var writer = _connection.GetMessageWriter();
        writer.WriteSignalHeader(path: ObjectPath, @interface: PlayerInterface, member: "Seeked", signature: "x");
        writer.WriteInt64(Microseconds(position));
        _connection.TrySendMessage(writer.CreateMessage());
    }

    // ---- Artwork -----------------------------------------------------------

    // MPRIS takes art as a URL, and the desktop reads it itself, so the bytes
    // go to a file: one per cover, named by its content, the previous one
    // deleted when the song changes. XDG_RUNTIME_DIR where there is one (per
    // user, memory-backed, cleared at logout), the temp folder otherwise.
    private static string DefaultArtDirectory() =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath(),
            "flower-now-playing");

    // Under _gate.
    private string? WriteArt(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 })
            return null;

        try
        {
            Directory.CreateDirectory(_artDirectory);
            var path = Path.Combine(_artDirectory, $"cover-{Convert.ToHexStringLower(SHA256.HashData(bytes), 0, 8)}");
            if (!File.Exists(path))
                File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogDebug(ex, "Could not write now-playing art for MPRIS");
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cover left behind in a per-user runtime folder is harmless.
        }
    }

    private static readonly ReadOnlyMemory<byte> IntrospectionXml = """
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek"><arg name="Offset" type="x" direction="in"/></method>
          <method name="SetPosition"><arg name="TrackId" type="o" direction="in"/><arg name="Position" type="x" direction="in"/></method>
          <method name="OpenUri"><arg name="Uri" type="s" direction="in"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="Rate" type="d" access="read"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Volume" type="d" access="read"/>
          <property name="Position" type="x" access="read"/>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
        </interface>
        <interface name="org.freedesktop.DBus.Properties">
          <method name="Get"><arg name="interface_name" type="s" direction="in"/><arg name="property_name" type="s" direction="in"/><arg name="value" type="v" direction="out"/></method>
          <method name="GetAll"><arg name="interface_name" type="s" direction="in"/><arg name="properties" type="a{sv}" direction="out"/></method>
          <method name="Set"><arg name="interface_name" type="s" direction="in"/><arg name="property_name" type="s" direction="in"/><arg name="value" type="v" direction="in"/></method>
          <signal name="PropertiesChanged"><arg name="interface_name" type="s"/><arg name="changed_properties" type="a{sv}"/><arg name="invalidated_properties" type="as"/></signal>
        </interface>
        """u8.ToArray();
}
