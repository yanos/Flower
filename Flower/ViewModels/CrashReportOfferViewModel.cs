using System;

using CommunityToolkit.Mvvm.Input;

using Flower.Logging;
using Flower.Services;

namespace Flower.ViewModels;

// The banner a launch shows after a crash (PendingCrash): what happened, a
// link to report it on GitHub with the details already written (IssueLink),
// and a close button. The link itself is opened by the view - a
// HyperlinkButton, through the platform's own launcher, which is what reaches
// a browser on a phone as well as a desktop - so all this has to do is
// forget the crash once it is answered either way, and close.
//
// Answered means gone for good: the next launch does not ask again. Ignored -
// the app quit with the banner still up - means asked again next time.
public sealed partial class CrashReportOfferViewModel : ViewModelBase
{
    private readonly Action _forget;

    public CrashReportOfferViewModel(PendingCrash crash, string version, string platform, string? homeDirectory, Action? forget = null)
    {
        _forget = forget ?? PendingCrash.Clear;
        IssueUrl = IssueLink.ForCrash(crash, version, platform, homeDirectory);
        Message = $"Flower quit unexpectedly last time ({crash.Summary.Split('\n', 2)[0]}). Reporting it helps get it fixed.";
    }

    public string Message { get; }

    public Uri IssueUrl { get; }

    private bool _isOpen = true;
    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    // Bound by the report link as well as the close button: once the issue
    // page is open, the banner has done its job.
    [RelayCommand]
    private void Answer()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        _forget();
    }
}
