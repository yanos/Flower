using System;
using System.Net.Http;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Flower.Services;

namespace Flower.ViewModels;

// "Generate Pairing Code" on mobile's Settings sheet: the same one-time code
// the server hands out through its own settings screen (see
// SettingsViewModel's IssuePairingCodeCommand and PairingCodeService), and
// what came of asking for one. Its own class rather than a region of
// MainViewModel (docs/CODE-REVIEW-2026-09.md E3), which decides whether there
// is a server to ask (CanInviteDeviceToPairedServer) and how to reach it.
//
// Desktop no longer has a second one of these. It used to sit in the
// device-detail header, next to a pane that browsed the server's catalog;
// that pane now *is* the server's settings screen (SelectedServerSettings),
// whose Devices tab already offers this - and two identical buttons on one
// page is worse than the trip the header one was saving. A phone has no such
// pane, which is why this state stays.
public sealed class PairingCodeIssuerViewModel : ViewModelBase
{
    // The code last handed out, for reading out loud.
    private string? _code;
    public string? Code
    {
        get => _code;
        private set => SetProperty(ref _code, value);
    }

    // The same code rendered as a flower:// link, which additionally carries
    // the server's fingerprint. Kept beside the bare code rather than
    // replacing it: five characters is what survives being read down a phone
    // line, but only the link lets the device on the other end check which
    // server it is pairing with, so the stronger of the two has to be on
    // screen for anyone to use it - see PairingEntry.
    private string? _invite;
    public string? Invite
    {
        get => _invite;
        private set => SetProperty(ref _invite, value);
    }

    // "Paired, but not an administrator of that server" is the most likely
    // answer here, and an ordinary outcome of pressing the button rather than
    // a fault worth a dialog.
    private string? _error;
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    private bool _isIssuing;
    public bool IsIssuing
    {
        get => _isIssuing;
        private set => SetProperty(ref _isIssuing, value);
    }

    // Whether the code the next press asks for should grant administrator
    // rights - the checkbox next to the button.
    //
    // Off by default and deliberately not remembered between presses: adding
    // a listener's phone is the ordinary case, and a checkbox that stayed
    // ticked from an hour ago would quietly turn the next guest into an
    // administrator. More than one device may hold it at once, though -
    // somebody's own desktop and their own phone both being admins is the
    // expected shape, not an exception (see TrustedPeerStore.TrustedPeer.IsAdmin).
    private bool _grantsAdmin;
    public bool GrantsAdmin
    {
        get => _grantsAdmin;
        set => SetProperty(ref _grantsAdmin, value);
    }

    // A code handed out for one server is worthless against another, and
    // leaving it on screen under a different server's name says otherwise.
    public void Clear()
    {
        Code = null;
        Invite = null;
        Error = null;
    }

    public async Task IssueAsync(Func<HttpClient, ServerAdminClient> clientFor, Uri server, ILogger logger)
    {
        Error = null;
        // The previous code is dropped before the request rather than after
        // it: a stale code left on screen next to a spinner reads as the new
        // one.
        Code = null;
        Invite = null;
        IsIssuing = true;
        try
        {
            using var http = PeerHttpClient.Create(TimeSpan.FromSeconds(15));
            var pairing = await clientFor(http).IssuePairingCodeAsync(GrantsAdmin);
            Code = pairing.Code;
            Invite = pairing.Invite;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not issue a pairing code on {Device}", server);
            Error = ex is ServerAdminException ? ex.Message : "Could not reach that server.";
        }
        finally
        {
            IsIssuing = false;
        }
    }
}
