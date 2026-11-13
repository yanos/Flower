using System;

using CommunityToolkit.Mvvm.Input;

using Flower.Persistence;
using Flower.Services;

namespace Flower.ViewModels;

// Tells a new install, once, that its crashes are reported - on by default
// (AppSettings.SendCrashReports), so the person should hear it from Flower
// rather than find it in Settings. Shown as a banner across a desktop's page
// and atop a phone's Home, with what is sent a link away (docs/PRIVACY.md)
// and the switch one tap away. Either answer is final: the notice is not
// shown again (AppSettings.CrashReportingNoticeSeen).
//
// Only offered where reports really leave (CrashReporting.Running): a
// pre-release sends nothing, so it says nothing, and the first release that
// does send is the one that asks.
public sealed partial class CrashReportingNoticeViewModel : ViewModelBase
{
    public const string PrivacyPage = "https://github.com/" + GitHubReleaseUpdater.Repository + "/blob/master/docs/PRIVACY.md";

    private readonly Action _turnOff;
    private readonly Action _seen;

    private CrashReportingNoticeViewModel(Action turnOff, Action seen)
    {
        _turnOff = turnOff;
        _seen = seen;
    }

    // The notice this launch shows, or null when there is none to show: told
    // before, or reporting already off.
    public static CrashReportingNoticeViewModel? OfferFor(AppSettings settings, Action turnOff, Action save)
    {
        if (settings.CrashReportingNoticeSeen || !settings.SendCrashReports)
            return null;

        return new CrashReportingNoticeViewModel(turnOff, () =>
        {
            settings.CrashReportingNoticeSeen = true;
            save();
        });
    }

    public string Message =>
        "Flower sends anonymous crash reports to its developer, so crashes get fixed. They never include your name, your music or your files.";

    public Uri PrivacyUrl { get; } = new(PrivacyPage);

    private bool _isOpen = true;
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    // The close button: reporting stays on, and that is the answer.
    [RelayCommand]
    private void Keep() => Close();

    [RelayCommand]
    private void TurnOff()
    {
        _turnOff();
        Close();
    }

    // Also how Settings' own switch answers it: turning reporting off there
    // leaves nothing for the notice to say.
    public void Close()
    {
        if (!IsOpen)
            return;

        IsOpen = false;
        _seen();
    }
}
