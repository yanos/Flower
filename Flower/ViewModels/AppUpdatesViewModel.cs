using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Persistence;
using Flower.Services;

using Microsoft.Extensions.Logging;

namespace Flower.ViewModels;

// When a desktop installed from a release looks for a newer one, and what it
// says when there is one - docs/AUTO-UPDATE-PLAN.md, Phase 3. Where the new
// version comes from and how it goes in are the head's (IAppUpdater); this is
// the part every head shares: at most one check a day, remembered across
// launches (AppSettings.LastUpdateCheck), none at all with the setting off,
// and an update found becomes a banner across the window that asks rather
// than acts - nothing restarts on its own while someone is listening.
//
// Only exists where there is an updater (MainViewModel.Updates is null
// otherwise), so the banner and the Settings rows need no platform checks of
// their own.
public sealed class AppUpdatesViewModel : ViewModelBase
{
    // How often a running app asks whether a check is due. The check itself
    // is once a day; this is only how soon after the day is up it notices,
    // for an app left open for a week.
    private static readonly TimeSpan Poll = TimeSpan.FromHours(1);

    // Long enough that the startup rescan and the first sync have the
    // network and the disk to themselves.
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    private readonly IAppUpdater _updater;
    private readonly AppSettings _settings;
    private readonly Func<Task> _saveSettings;
    private readonly ILogger<AppUpdatesViewModel> _logger;
    private readonly TimeProvider _clock;

    public AppUpdatesViewModel(
        IAppUpdater updater,
        AppSettings settings,
        Func<Task> saveSettings,
        ILogger<AppUpdatesViewModel> logger,
        TimeProvider? clock = null)
    {
        _updater = updater;
        _settings = settings;
        _saveSettings = saveSettings;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        CheckNowCommand = new AsyncRelayCommand(() => CheckAsync(manual: true), () => !IsChecking);
        InstallCommand = new RelayCommand(() => Available?.Install());
        DismissCommand = new RelayCommand(() => IsBannerDismissed = true);
        Status = DescribeLastCheck();
    }

    // Settings > General's switch. Turning it on does not check there and
    // then; the next poll does, if a day has gone by.
    public bool CheckAutomatically
    {
        get => _settings.CheckForUpdatesAutomatically;
        set
        {
            if (_settings.CheckForUpdatesAutomatically == value)
                return;
            _settings.CheckForUpdatesAutomatically = value;
            _ = _saveSettings();
            OnPropertyChanged();
        }
    }

    // The newer release found, ready to install. Once there is one nothing
    // looks again this run: it is already downloaded where that applies, and
    // one release behind is the most this app ever needs to say.
    private AppUpdate? _available;
    public AppUpdate? Available
    {
        get => _available;
        private set
        {
            _available = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowsBanner));
        }
    }

    // The banner's close button lasts until the next launch. Settings still
    // says an update is there, with its button.
    private bool _isBannerDismissed;
    public bool IsBannerDismissed
    {
        get => _isBannerDismissed;
        private set
        {
            _isBannerDismissed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowsBanner));
        }
    }

    public bool ShowsBanner => Available != null && !IsBannerDismissed;

    private bool _isChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            _isChecking = value;
            OnPropertyChanged();
            ((AsyncRelayCommand)CheckNowCommand).NotifyCanExecuteChanged();
        }
    }

    // The line beside Check Now: what the last check found, or why it could
    // not tell.
    private string _status = "";
    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public ICommand CheckNowCommand { get; }
    public ICommand InstallCommand { get; }
    public ICommand DismissCommand { get; }

    public bool IsDue =>
        CheckAutomatically
        && Available is null
        && (_settings.LastUpdateCheck is not { } last || _clock.GetUtcNow() - last >= CheckInterval);

    // Runs for the life of the app, from the UI thread so what it finds lands
    // there.
    public async void Start(CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, _clock, ct);
            while (!ct.IsCancellationRequested)
            {
                await CheckIfDueAsync(ct);
                await Task.Delay(Poll, _clock, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public Task CheckIfDueAsync(CancellationToken ct = default) =>
        IsDue ? CheckAsync(manual: false, ct) : Task.CompletedTask;

    // A failed check leaves LastUpdateCheck where it was, so the next poll
    // tries again rather than a day later. Only one asked for by hand says
    // why in Settings; an automatic one that fails offline is not news.
    private async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (IsChecking || Available != null)
            return;

        IsChecking = true;
        Status = "Checking for updates…";
        try
        {
            var update = await _updater.FindAsync(ct);
            _settings.LastUpdateCheck = _clock.GetUtcNow();
            _ = _saveSettings();
            if (update != null)
            {
                _logger.LogInformation("Flower {Version} is available (running {Current})", update.Version, AppVersion.Display);
                Available = update;
                Status = update.Description;
            }
            else
            {
                _logger.LogDebug("No newer release than {Current}", AppVersion.Display);
                Status = DescribeLastCheck();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = DescribeLastCheck();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Checking for updates failed");
            Status = manual ? $"Couldn't check for updates: {ex.Message}" : DescribeLastCheck();
        }
        finally
        {
            IsChecking = false;
        }
    }

    private string DescribeLastCheck() => _settings.LastUpdateCheck is { } last
        ? $"Flower {AppVersion.Display} is up to date. Last checked {last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}."
        : $"Flower {AppVersion.Display}. Not checked for updates yet.";
}
