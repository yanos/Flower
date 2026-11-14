using System;
using System.Diagnostics;
using System.Runtime.Versioning;

using Avalonia.Threading;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Services;

namespace Flower.Platform;

// Windows' half of docs/MEDIA-KEYS-PLAN.md, over
// WindowsMediaTransportControls. The controls belong to a window, and the
// window does not exist when the head sets PlatformNowPlaying.Current, so they
// are opened on the first update, from whatever the head's window lookup
// returns then; an update before there is a window is dropped, and the next
// one tries again.
//
// Every call lands on the UI thread, the window's thread, whichever thread
// NowPlayingIntegrationService was called on (IAudioManager's events come
// from the audio side). The button presses come back on a thread of
// Windows' choosing; NowPlayingIntegrationService marshals those itself.
[SupportedOSPlatform("windows10.0.10240")]
public sealed class WindowsNowPlaying : IPlatformNowPlaying
{
    // The flyout draws its own progress bar from the timeline and moves it on
    // by itself while the status says Playing, so the position is restated
    // only when something changes or this often, never on every tick.
    private static readonly TimeSpan TimelineRefresh = TimeSpan.FromSeconds(5);

    private readonly Func<IntPtr?> _windowHandle;
    private WindowsMediaTransportControls? _controls;
    private bool _failed;
    private ILogger? _logger;

    private volatile bool _isPlaying;
    private TimeSpan _duration;
    private long _lastTimelineStamp;

    public event EventHandler<NowPlayingCommand>? CommandReceived;

    public WindowsNowPlaying(Func<IntPtr?> windowHandle) => _windowHandle = windowHandle;

    private ILogger Logger => _logger ??= AppLogging.CreateLogger<WindowsNowPlaying>();

    public void UpdateMetadata(NowPlayingMetadata metadata) => OnUiThread(controls =>
    {
        _duration = metadata.Duration;
        controls.SetMusic(metadata.Title, metadata.Artist, metadata.Album, metadata.ArtworkData);
        _lastTimelineStamp = 0;
    });

    public void UpdatePlaybackState(bool isPlaying, TimeSpan elapsed) => OnUiThread(controls =>
    {
        var changed = _isPlaying != isPlaying || controls.PlaybackStatus is not (MediaPlaybackStatus.Playing or MediaPlaybackStatus.Paused);
        _isPlaying = isPlaying;
        if (changed)
            controls.PlaybackStatus = isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

        if (changed || _lastTimelineStamp == 0 || Stopwatch.GetElapsedTime(_lastTimelineStamp) >= TimelineRefresh)
        {
            controls.SetTimeline(elapsed, _duration);
            _lastTimelineStamp = Stopwatch.GetTimestamp();
        }
    });

    public void Clear() => OnUiThread(controls =>
    {
        _isPlaying = false;
        controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
        controls.ClearMusic();
    });

    private void OnUiThread(Action<WindowsMediaTransportControls> update)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Controls() is not { } controls)
                return;
            try
            {
                update(controls);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Could not update Windows' media controls");
            }
        });
    }

    private WindowsMediaTransportControls? Controls()
    {
        if (_controls != null || _failed)
            return _controls;
        if (_windowHandle() is not { } hwnd || hwnd == IntPtr.Zero)
            return null;

        try
        {
            _controls = WindowsMediaTransportControls.ForWindow(hwnd);
            _controls.ButtonPressed += OnButtonPressed;
        }
        catch (Exception ex)
        {
            // Once: a Windows without the controls will not grow them.
            _failed = true;
            Logger.LogWarning(ex, "Windows' media controls are unavailable; media keys will not reach Flower");
        }
        return _controls;
    }

    // Windows has Play and Pause as separate buttons (a keyboard's one
    // play/pause key arrives as whichever the state calls for), Flower one
    // toggle. Each only moves playback the way it says.
    private void OnButtonPressed(MediaTransportButton button)
    {
        NowPlayingCommand? command = button switch
        {
            MediaTransportButton.Play => _isPlaying ? null : NowPlayingCommand.PlayPause,
            MediaTransportButton.Pause or MediaTransportButton.Stop => _isPlaying ? NowPlayingCommand.PlayPause : null,
            MediaTransportButton.Next => NowPlayingCommand.Next,
            MediaTransportButton.Previous => NowPlayingCommand.Previous,
            _ => null
        };
        if (command is { } c)
            CommandReceived?.Invoke(this, c);
    }
}
