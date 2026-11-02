using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Avalonia.Headless.XUnit;

using Flower.Logging;
using Flower.SemanticAudio;
using Flower.Services;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests.ViewModels.Settings;

// The AI Features tab on a server's settings page
// (docs/VIBE-PLAYLIST-PLAN.md, "The settings page"): a master switch with
// Vibe Playlist's and Endless Play's under it, drafted like the rest of the
// page, the last of them turned off saying what saving deletes, the server's
// live status in words, and a pause that applies at once.
public class SettingsAiFeaturesTests
{
    private const long Megabyte = 1_000_000;

    private sealed class Backend : ISettingsBackend
    {
        public SettingsSnapshot Snapshot { get; set; } = new() { AiFeaturesEnabled = true, VibeEnabled = true, VibeClipsPerSong = 6 };
        public Exception? StatusFailure { get; set; }
        public VibeStatusDto Status { get; set; } = Report("UpToDate");
        public List<SettingsDraft> Saved { get; } = [];
        public int Retries { get; private set; }
        public List<bool> Pauses { get; } = [];

        public SettingsCapabilities Capabilities { get; } = new() { AiFeatures = true };

        public Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default) => Task.FromResult(Snapshot);

        public Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default)
        {
            Saved.Add(draft);
            return Task.FromResult<string?>(null);
        }

        public Task<VibeStatusDto> LoadVibeStatusAsync(CancellationToken ct = default) =>
            StatusFailure is { } failure ? Task.FromException<VibeStatusDto>(failure) : Task.FromResult(Status);

        public Task<VibeStatusDto> RetryVibeAsync(CancellationToken ct = default)
        {
            Retries++;
            Status = Report("Analysing");
            return Task.FromResult(Status);
        }

        public Task<VibeStatusDto> SetVibePausedAsync(bool paused, CancellationToken ct = default)
        {
            Pauses.Add(paused);
            Status = Report(paused ? "Paused" : "Analysing");
            return Task.FromResult(Status);
        }

        public int CountSongsUnder(string folder) => 0;
        public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(string Code, string Invite)> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RescanAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RebuildDatabaseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<LogSlice> LoadLogAsync(int limit, long afterSequence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InMemoryLogEntry>?> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<RemovedFileRow>> LoadRemovedFilesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RemovedFileRow>>([]);
        public Task<string> RestoreRemovedFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> DeleteRemovedFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
    }

    // 16,000 songs heard, at 6 clips, 4 clips a second.
    private static VibeStatusDto Report(string state, string? error = null, VibeDownloadDto? download = null) => new(
        state, download, SongsDone: 4000, SongsEligible: 16000, SongsSearchable: 16000,
        CurrentTitle: state == "Analysing" ? "Riders on the Storm" : null, CurrentArtist: state == "Analysing" ? "The Doors" : null,
        SecondsLeft: state == "Analysing" ? 3 * 3600 : null, error,
        ModelBytes: 330 * Megabyte, AnalysisBytes: 94 * Megabyte,
        Costs: ClipGrid.Settings.Select(clips => new VibeClipCostDto(clips, clips * 16000L, clips * 16000L * 1024, clips * 16000 / 4.0)).ToList(),
        FailedCount: 0, Failures: []);

    private static async Task<(Backend, SettingsViewModel)> OpenAsync(Action<Backend>? arrange = null)
    {
        var backend = new Backend();
        arrange?.Invoke(backend);
        var viewModel = new SettingsViewModel(backend);
        await viewModel.LoadAsync(TestContext.Current.CancellationToken);
        return (backend, viewModel);
    }

    [AvaloniaFact]
    public async Task Turning_the_last_feature_off_says_what_saving_deletes_until_it_is_turned_back_on()
    {
        var (_, settings) = await OpenAsync();
        Assert.Null(settings.AiFeatures.OffWarning);

        settings.AiFeatures.VibeEnabled = false;

        Assert.Contains("deletes the model (330 MB) and the analysis of 16,000 songs", settings.AiFeatures.OffWarning);
        Assert.Contains("about 7 h", settings.AiFeatures.OffWarning);

        settings.AiFeatures.VibeEnabled = true;

        Assert.Null(settings.AiFeatures.OffWarning);
    }

    [AvaloniaFact]
    public async Task Turning_ai_features_off_turns_vibe_playlist_off_with_them()
    {
        var (_, settings) = await OpenAsync();

        settings.AiFeatures.Enabled = false;

        Assert.NotNull(settings.AiFeatures.OffWarning);
        Assert.False(settings.AiFeatures.CanEditVibe);
        Assert.False(settings.AiFeatures.CanEditEndlessPlay);
        Assert.False(settings.AiFeatures.CanEditClips);
    }

    [AvaloniaFact]
    public async Task Either_feature_keeps_the_listening_so_only_the_last_one_off_deletes_it()
    {
        var (_, settings) = await OpenAsync(b => b.Snapshot = b.Snapshot with { EndlessPlayEnabled = true });

        settings.AiFeatures.VibeEnabled = false;
        Assert.Null(settings.AiFeatures.OffWarning);
        Assert.True(settings.AiFeatures.CanEditClips);

        settings.AiFeatures.EndlessPlayEnabled = false;
        Assert.NotNull(settings.AiFeatures.OffWarning);
        Assert.False(settings.AiFeatures.CanEditClips);
    }

    [AvaloniaFact]
    public async Task Endless_play_alone_starts_the_listening()
    {
        var (_, settings) = await OpenAsync(b =>
        {
            b.Snapshot = new() { AiFeaturesEnabled = true, VibeEnabled = false };
            b.Status = Report("Off");
        });
        Assert.Null(settings.AiFeatures.OnNote);

        settings.AiFeatures.EndlessPlayEnabled = true;

        Assert.Contains("downloads the model", settings.AiFeatures.OnNote);
    }

    [AvaloniaFact]
    public async Task A_server_from_before_ai_features_says_it_needs_updating()
    {
        var (_, settings) = await OpenAsync(b =>
            b.StatusFailure = new ServerAdminException(System.Net.HttpStatusCode.NotFound, "Nothing here by that name."));

        Assert.True(settings.AiFeatures.IsUnsupported);
        Assert.False(settings.AiFeatures.CanEdit);
        Assert.Contains("Update it", settings.AiFeatures.StatusError);
        Assert.Null(settings.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task Saving_sends_every_setting_with_the_rest_of_the_page()
    {
        var (backend, settings) = await OpenAsync();
        settings.AiFeatures.Choices.Single(c => c.ClipsPerSong == ClipGrid.WholeSong).IsSelected = true;
        settings.AiFeatures.VibeEnabled = false;
        settings.AiFeatures.EndlessPlayEnabled = true;

        await settings.SaveAsync(TestContext.Current.CancellationToken);

        var draft = Assert.Single(backend.Saved);
        Assert.True(draft.AiFeaturesEnabled);
        Assert.False(draft.VibeEnabled);
        Assert.True(draft.EndlessPlayEnabled);
        Assert.Equal(ClipGrid.WholeSong, draft.VibeClipsPerSong);
        Assert.Null(settings.AiFeatures.OffWarning);
    }

    [AvaloniaFact]
    public async Task Turning_ai_features_on_says_what_saving_will_download()
    {
        var (_, settings) = await OpenAsync(b =>
        {
            b.Snapshot = new() { AiFeaturesEnabled = false };
            b.Status = Report("Off");
        });
        Assert.False(settings.AiFeatures.CanEditVibe);

        settings.AiFeatures.Enabled = true;

        Assert.Contains("downloads the model", settings.AiFeatures.OnNote);
        Assert.False(settings.AiFeatures.ShowsStatus);
    }

    [AvaloniaFact]
    public async Task Only_raising_the_clip_setting_says_it_listens_again()
    {
        var (_, settings) = await OpenAsync();

        settings.AiFeatures.ClipsPerSong = 3;
        Assert.Null(settings.AiFeatures.RaiseNote);

        settings.AiFeatures.ClipsPerSong = ClipGrid.WholeSong;
        Assert.NotNull(settings.AiFeatures.RaiseNote);
    }

    [AvaloniaFact]
    public async Task Each_clip_setting_says_what_it_costs_here()
    {
        var (_, settings) = await OpenAsync();

        var six = settings.AiFeatures.Choices.Single(c => c.ClipsPerSong == 6);

        Assert.Equal("98 MB · about 7 h", six.CostText);
        Assert.True(six.IsSelected);
    }

    [AvaloniaFact]
    public async Task Says_where_the_analysis_is_and_what_it_is_listening_to()
    {
        var (_, settings) = await OpenAsync(b => b.Status = Report("Analysing"));

        Assert.Equal("Analysing: 25% (4,000 of 16,000 songs), about 3 h left", settings.AiFeatures.StatusText);
        Assert.Equal("Now: Riders on the Storm by The Doors", settings.AiFeatures.NowText);
        Assert.Equal(25, settings.AiFeatures.Progress);
        Assert.Equal("Using 424 MB (model 330 MB, analysis 94 MB)", settings.AiFeatures.StorageText);
    }

    [AvaloniaFact]
    public async Task Says_how_far_the_download_is()
    {
        var (_, settings) = await OpenAsync(b => b.Status = Report("Downloading", download: new VibeDownloadDto(110 * Megabyte, 330 * Megabyte)));

        Assert.Equal("Downloading the model: 33% (110 MB of 330 MB)", settings.AiFeatures.StatusText);
        Assert.Equal(33, settings.AiFeatures.Progress);
    }

    [AvaloniaFact]
    public async Task An_error_is_shown_with_a_retry_that_asks_the_server_again()
    {
        var (backend, settings) = await OpenAsync(b => b.Status = Report("Failed", error: "Couldn't download the model: offline"));
        Assert.True(settings.AiFeatures.IsFailed);
        Assert.Equal("Couldn't download the model: offline", settings.AiFeatures.StatusText);

        await settings.AiFeatures.RetryCommand.ExecuteAsync(null);

        Assert.Equal(1, backend.Retries);
        Assert.False(settings.AiFeatures.IsFailed);
    }

    [AvaloniaFact]
    public async Task Pause_stops_the_listening_at_once_and_resume_starts_it_again()
    {
        var (backend, settings) = await OpenAsync(b => b.Status = Report("Analysing"));
        Assert.True(settings.AiFeatures.CanPause);
        Assert.False(settings.AiFeatures.CanResume);

        await settings.AiFeatures.PauseCommand.ExecuteAsync(null);

        Assert.Equal([true], backend.Pauses);
        Assert.Equal("Paused: 25% (4,000 of 16,000 songs)", settings.AiFeatures.StatusText);
        Assert.False(settings.AiFeatures.CanPause);
        Assert.True(settings.AiFeatures.CanResume);
        Assert.Empty(backend.Saved);

        await settings.AiFeatures.ResumeCommand.ExecuteAsync(null);

        Assert.Equal([true, false], backend.Pauses);
        Assert.True(settings.AiFeatures.CanPause);
    }

    [AvaloniaFact]
    public async Task There_is_nothing_to_pause_once_every_song_is_heard()
    {
        var (_, settings) = await OpenAsync();

        Assert.False(settings.AiFeatures.CanPause);
        Assert.False(settings.AiFeatures.CanResume);
    }

    [AvaloniaFact]
    public async Task Waiting_for_a_scan_is_not_the_owner_pausing()
    {
        var (_, settings) = await OpenAsync(b => b.Status = Report("WaitingForScan"));

        Assert.Equal("Waiting for the library scan to finish: 25% (4,000 of 16,000 songs)", settings.AiFeatures.StatusText);
        Assert.True(settings.AiFeatures.CanPause);
        Assert.False(settings.AiFeatures.CanResume);
    }

    // A paired device's own Endless Play switch: usable while its server
    // offers it, and greyed with a line saying why while it doesn't.
    [AvaloniaTheory]
    [InlineData(true, true, null)]
    [InlineData(false, false, "Endless Play is off on Lambda. Its owner can turn it on in its AI Features settings.")]
    [InlineData(null, false, null)]
    public async Task A_device_offers_endless_play_where_its_server_does(bool? offered, bool editable, string? note)
    {
        var (_, settings) = await OpenAsync(b => b.Snapshot = b.Snapshot with
        {
            IsPairedToServer = true, PairedServerName = "Lambda", PairedServerEndlessPlay = offered,
        });

        Assert.Equal(editable, settings.CanEditEndlessPlay);
        Assert.Equal(note, settings.EndlessPlayNote);
    }

    // A paired device's own settings: one line about its server, which is
    // the one that makes Vibe Playlists for it.
    [AvaloniaTheory]
    [InlineData("ready", "Vibe Playlists are made by Lambda, which has them on.")]
    [InlineData("analysing", "Vibe Playlists are made by Lambda, which has them on.")]
    [InlineData("off", "Vibe Playlists are made by Lambda, which has them off.")]
    [InlineData(null, null)]
    public async Task A_paired_device_says_whether_its_server_makes_vibe_playlists(string? state, string? says)
    {
        var (_, settings) = await OpenAsync(b => b.Snapshot = b.Snapshot with
        {
            IsPairedToServer = true, PairedServerName = "Lambda", PairedServerVibePlaylists = state,
        });

        Assert.Equal(says, settings.VibePlaylistsNote);
    }

    [AvaloniaFact]
    public async Task A_device_on_its_own_has_no_such_line()
    {
        var (_, settings) = await OpenAsync(b => b.Snapshot = b.Snapshot with { PairedServerVibePlaylists = "ready" });

        Assert.Null(settings.VibePlaylistsNote);
    }
}
