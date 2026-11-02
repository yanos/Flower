using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Flower.SemanticAudio;
using Flower.Services;

namespace Flower.ViewModels;

// The AI Features tab: the master switch over every feature that runs a
// model on the host's own songs, and under it each feature's own - for now
// only Vibe Playlist (docs/VIBE-PLAYLIST-PLAN.md, "The settings page"), with
// its clips per song and where its job stands. The switches are drafted with
// the rest of the page and saved by its Save; the status is live, polled
// every two seconds while the tab shows, as the Logs tab follows its log.
//
// A feature runs while both its switch and the master are on, so either one
// turned off deletes hours of work. A switch turned off says, under itself,
// what saving will delete - the page's Save is the confirmation, and Cancel
// or the switch turned back on is the "no".
public sealed partial class AiFeaturesSettingsViewModel : ViewModelBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly ISettingsBackend _backend;
    private DispatcherTimer? _timer;
    private bool _pollInFlight;
    private bool _savedEnabled;
    private bool _savedVibeEnabled = true;
    private int _savedClipsPerSong = ClipGrid.DefaultClipsPerSong;
    private VibeStatusDto? _status;

    public AiFeaturesSettingsViewModel(ISettingsBackend backend)
    {
        _backend = backend;
        Choices = ClipGrid.Settings.Select(clips => new VibeClipChoice(this, clips)).ToList();
    }

    // The clip settings offered, each with what it costs here.
    public IReadOnlyList<VibeClipChoice> Choices { get; }

    // The master switch.
    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value))
                return;

            OnPropertyChanged(nameof(CanEditVibe));
            OnDraftChanged();
        }
    }

    private bool _vibeEnabled = true;
    public bool VibeEnabled
    {
        get => _vibeEnabled;
        set
        {
            if (SetProperty(ref _vibeEnabled, value))
                OnDraftChanged();
        }
    }

    private int _clipsPerSong = ClipGrid.DefaultClipsPerSong;
    public int ClipsPerSong
    {
        get => _clipsPerSong;
        set
        {
            if (!SetProperty(ref _clipsPerSong, value))
                return;

            foreach (var choice in Choices)
                choice.Refresh();
            OnDraftChanged();
        }
    }

    public SettingLock EnabledLock { get; private set; } = SettingLock.None;
    public SettingLock VibeEnabledLock { get; private set; } = SettingLock.None;
    public SettingLock ClipsPerSongLock { get; private set; } = SettingLock.None;

    // A host older than these settings answers their status with a 404:
    // there is nothing here to switch on until it is updated.
    private bool _isUnsupported;
    public bool IsUnsupported
    {
        get => _isUnsupported;
        private set
        {
            if (!SetProperty(ref _isUnsupported, value))
                return;

            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanEditVibe));
        }
    }

    public bool CanEdit => !IsUnsupported && EnabledLock.IsEditable;

    // A feature's own switch and settings mean something only under the
    // master switch.
    public bool CanEditVibe => !IsUnsupported && Enabled && VibeEnabledLock.IsEditable;
    public bool CanEditClips => CanEditVibe && ClipsPerSongLock.IsEditable;

    public string Description =>
        "Features that run a model on this server, on its own songs. Nothing leaves the server. "
        + "Turning this off deletes every model and everything they worked out.";

    public string VibeDescription =>
        "Make a playlist from a sentence, like “music for a rainy day”: the server listens to every song once and "
        + $"plays the ones that sound like it. Turning this on downloads a {Size(SemanticAudioModelManifest.Current.TotalBytes)} model first.";

    public string ClipsDescription =>
        "How much of each song is listened to: a few 10-second clips spread over it, or all of it. "
        + "More clips take longer and found no better songs in testing. Lowering it is instant; raising it listens only to the clips not heard before.";

    private bool SavedRunsVibe => _savedEnabled && _savedVibeEnabled;
    private bool RunsVibe => Enabled && VibeEnabled;

    // What saving will do that can't be undone, or will take a long time.
    public string? OffWarning
    {
        get
        {
            if (!SavedRunsVibe || RunsVibe)
                return null;

            var model = _status?.ModelBytes ?? 0;
            var songs = _status?.SongsSearchable ?? 0;
            var again = SavedCost?.Seconds is { } seconds ? $" (about {Duration(seconds)} on this server)" : "";
            return $"Saving turns Vibe Playlist off and deletes its model ({Size(model)}) and the analysis of {songs:N0} songs. "
                + $"Turning it back on means analysing every song again{again}.";
        }
    }

    public string? OnNote
    {
        get
        {
            if (SavedRunsVibe || !RunsVibe)
                return null;

            var cost = Choices.FirstOrDefault(c => c.ClipsPerSong == ClipsPerSong)?.Cost;
            var time = cost?.Seconds is { } seconds ? $", about {Duration(seconds)}" : "";
            return $"Saving downloads Vibe Playlist's model ({Size(SemanticAudioModelManifest.Current.TotalBytes)}) and then listens to every song once{time}. "
                + "Songs can be found as soon as they have been heard.";
        }
    }

    public string? RaiseNote =>
        SavedRunsVibe && RunsVibe && ClipsPerSong != _savedClipsPerSong && Rank(ClipsPerSong) > Rank(_savedClipsPerSong)
            ? "Saving listens to the clips not already heard, in the background. Searches go on meanwhile."
            : null;

    // Live, from the host: shown whenever Vibe Playlist runs as saved.
    public bool ShowsStatus => SavedRunsVibe && _status != null && State != SemanticAudioState.Off;

    public string StatusText
    {
        get
        {
            if (_status is not { } status)
                return "";

            var songs = $"{Percent(status.SongsDone, status.SongsEligible)}% ({status.SongsDone:N0} of {status.SongsEligible:N0} songs)";
            return State switch
            {
                SemanticAudioState.Downloading when status.Download is { } download =>
                    $"Downloading the model: {Percent(download.BytesDone, download.BytesTotal)}% ({Size(download.BytesDone)} of {Size(download.BytesTotal)})",
                SemanticAudioState.Downloading => "Downloading the model",
                SemanticAudioState.Preparing => "Getting ready to search",
                SemanticAudioState.Analysing =>
                    $"Analysing: {songs}" + (status.SecondsLeft is { } left ? $", about {Duration(left)} left" : ""),
                SemanticAudioState.UpToDate => $"Up to date: {status.SongsSearchable:N0} songs analysed",
                SemanticAudioState.Paused => $"Paused while the library is scanned: {songs}",
                SemanticAudioState.Failed => status.Error ?? "Stopped on an error.",
                _ => "",
            };
        }
    }

    public string? NowText => _status is { CurrentTitle: { Length: > 0 } title } status
        ? status.CurrentArtist is { Length: > 0 } artist ? $"Now: {title} by {artist}" : $"Now: {title}"
        : null;

    public bool ShowsProgress => ShowsStatus && State is SemanticAudioState.Downloading or SemanticAudioState.Analysing
        or SemanticAudioState.Paused or SemanticAudioState.Preparing;

    public bool IsProgressIndeterminate => State == SemanticAudioState.Preparing
        || State == SemanticAudioState.Downloading && _status?.Download is null;

    public double Progress => _status switch
    {
        { Download: { } download } when State == SemanticAudioState.Downloading => Percent(download.BytesDone, download.BytesTotal),
        { } status => Percent(status.SongsDone, status.SongsEligible),
        _ => 0,
    };

    public bool IsFailed => ShowsStatus && State == SemanticAudioState.Failed;

    public string? StorageText => _status is { } status && status.ModelBytes + status.AnalysisBytes > 0
        ? $"Using {Size(status.ModelBytes + status.AnalysisBytes)} (model {Size(status.ModelBytes)}, analysis {Size(status.AnalysisBytes)})"
        : null;

    public string? FailedText => _status is { FailedCount: > 0 } status
        ? status.FailedCount == 1 ? "1 song couldn't be analysed" : $"{status.FailedCount:N0} songs couldn't be analysed"
        : null;

    public IReadOnlyList<string> Failures => _status?.Failures
        .Select(f => $"{(f.Artist.Length > 0 ? $"{f.Artist} – " : "")}{f.Title}: {f.Reason}")
        .ToList() ?? [];

    // Why the status can't be shown: a host that predates these settings, or
    // a request that failed while nothing had been shown yet (a poll keeps
    // quiet about one dropped request once there is something on screen).
    private string? _statusError;
    public string? StatusError
    {
        get => _statusError;
        private set => SetProperty(ref _statusError, value);
    }

    internal VibeClipCostDto? CostOf(int clipsPerSong) => _status?.Costs.FirstOrDefault(c => c.ClipsPerSong == clipsPerSong);

    private VibeClipCostDto? SavedCost => CostOf(_savedClipsPerSong);

    private SemanticAudioState State =>
        Enum.TryParse<SemanticAudioState>(_status?.State, out var state) ? state : SemanticAudioState.Off;

    internal void Load(SettingsSnapshot snapshot)
    {
        _savedEnabled = snapshot.AiFeaturesEnabled;
        _savedVibeEnabled = snapshot.VibeEnabled;
        _savedClipsPerSong = snapshot.VibeClipsPerSong;
        EnabledLock = LockFor(snapshot, nameof(SettingsSnapshot.AiFeaturesEnabled));
        VibeEnabledLock = LockFor(snapshot, nameof(SettingsSnapshot.VibeEnabled));
        ClipsPerSongLock = LockFor(snapshot, nameof(SettingsSnapshot.VibeClipsPerSong));
        OnPropertyChanged(nameof(EnabledLock));
        OnPropertyChanged(nameof(VibeEnabledLock));
        OnPropertyChanged(nameof(ClipsPerSongLock));

        _enabled = snapshot.AiFeaturesEnabled;
        _vibeEnabled = snapshot.VibeEnabled;
        _clipsPerSong = snapshot.VibeClipsPerSong;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(VibeEnabled));
        OnPropertyChanged(nameof(ClipsPerSong));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditVibe));
        OnPropertyChanged(nameof(CanEditClips));
        foreach (var choice in Choices)
            choice.Refresh();
        OnDraftChanged();
        OnStatusChanged();
    }

    // The draft went through: what is saved is what is shown.
    internal void Saved()
    {
        _savedEnabled = Enabled;
        _savedVibeEnabled = VibeEnabled;
        _savedClipsPerSong = ClipsPerSong;
        OnDraftChanged();
        OnStatusChanged();
    }

    // Called by SettingsPanel as the tab comes and goes, as SetLogTabActive is.
    public void SetActive(bool active)
    {
        if (!_backend.Capabilities.AiFeatures)
            return;

        if (!active)
        {
            _timer?.Stop();
            return;
        }

        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = PollInterval };
            _timer.Tick += OnTick;
        }

        _timer.Start();
        _ = RefreshAsync();
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_pollInFlight)
            return;

        _pollInFlight = true;
        try
        {
            await RefreshAsync();
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    // One poll, which is the whole of what the timer does.
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!_backend.Capabilities.AiFeatures)
            return;

        try
        {
            Show(await _backend.LoadVibeStatusAsync(ct));
        }
        catch (ServerAdminException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            IsUnsupported = true;
            _timer?.Stop();
            StatusError = "This server's version has no AI features. Update it to turn them on.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusError = _status == null ? $"Couldn't read where Vibe Playlist stands: {ex.Message}" : null;
        }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        try
        {
            Show(await _backend.RetryVibeAsync());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusError = $"Couldn't retry: {ex.Message}";
        }
    }

    private void Show(VibeStatusDto status)
    {
        _status = status;
        IsUnsupported = false;
        StatusError = null;
        foreach (var choice in Choices)
            choice.Refresh();
        OnStatusChanged();
        OnDraftChanged();
    }

    private void OnDraftChanged()
    {
        OnPropertyChanged(nameof(CanEditClips));
        OnPropertyChanged(nameof(OffWarning));
        OnPropertyChanged(nameof(OnNote));
        OnPropertyChanged(nameof(RaiseNote));
    }

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(ShowsStatus));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NowText));
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(StorageText));
        OnPropertyChanged(nameof(FailedText));
        OnPropertyChanged(nameof(Failures));
    }

    private static SettingLock LockFor(SettingsSnapshot snapshot, string setting) =>
        snapshot.Overridden.TryGetValue(setting, out var source) ? new SettingLock(source) : SettingLock.None;

    // Whole song is the most, not the least.
    private static int Rank(int clipsPerSong) => clipsPerSong == ClipGrid.WholeSong ? int.MaxValue : clipsPerSong;

    private static int Percent(long done, long total) => total > 0 ? (int)(100 * Math.Clamp(done, 0, total) / total) : 0;

    // Decimal, as Finder and the release page count.
    internal static string Size(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1e9:0.0} GB"
        : $"{Math.Max(bytes, 0) / 1e6:0} MB";

    internal static string Duration(double seconds) => seconds switch
    {
        < 60 => "a minute",
        < 3600 => $"{Math.Round(seconds / 60):0} min",
        < 48 * 3600 => $"{Math.Round(seconds / 3600):0} h",
        _ => $"{Math.Round(seconds / 86400):0} days",
    };
}

// One of Vibe Playlist's clip settings on offer, as a radio button with its
// cost.
public sealed partial class VibeClipChoice(AiFeaturesSettingsViewModel owner, int clipsPerSong) : ObservableObject
{
    public int ClipsPerSong { get; } = clipsPerSong;

    public string Label => ClipsPerSong == ClipGrid.WholeSong ? "Whole song" : $"{ClipsPerSong} clips";

    public bool IsSelected
    {
        get => owner.ClipsPerSong == ClipsPerSong;
        set
        {
            if (value)
                owner.ClipsPerSong = ClipsPerSong;
        }
    }

    internal VibeClipCostDto? Cost => owner.CostOf(ClipsPerSong);

    public string CostText => Cost is { } cost
        ? AiFeaturesSettingsViewModel.Size(cost.Bytes) + (cost.Seconds is { } seconds ? $" · about {AiFeaturesSettingsViewModel.Duration(seconds)}" : "")
        : "";

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(CostText));
    }
}
