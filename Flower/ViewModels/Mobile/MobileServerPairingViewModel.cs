using System;
using System.Windows.Input;

using CommunityToolkit.Mvvm.Input;

using Flower.Services;

namespace Flower.ViewModels.Mobile;

// Pairing with a server and unpairing from it, from the phone's Settings: the
// two confirmation sheets, the code box, and the server added by address. Its
// own class rather than a region of MobileMainViewModel
// (docs/CODE-REVIEW-2026-09.md E3). What it needs of the shell is one thing:
// which sheet is up, since each confirmation takes Settings' place and puts it
// back whichever way it is answered.
public sealed class MobileServerPairingViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Func<MobileSheet> _getSheet;
    private readonly Action<MobileSheet> _setSheet;

    public MobileServerPairingViewModel(MainViewModel main, Func<MobileSheet> getSheet, Action<MobileSheet> setSheet)
    {
        _main = main;
        _getSheet = getSheet;
        _setSheet = setSheet;

        // Confirm-before-pairing (see ConfirmPairServerMessage) rather than
        // pairing immediately - matches desktop's ServerPickerView dialog.
        PairWithServerCommand = new RelayCommand<DiscoveredDevice>(device =>
        {
            if (device == null)
                return;
            _pendingServerToPair = device;
            _sheetBeforePairing = _getSheet();
            PendingPairingCode = "";
            OnPropertyChanged(nameof(PendingServerToPairAlias));
            OnPropertyChanged(nameof(ConfirmPairServerTitle));
            OnPropertyChanged(nameof(ConfirmPairServerActionLabel));
            OnPropertyChanged(nameof(ConfirmPairServerMessage));
            OnPropertyChanged(nameof(IsConfirmPairServerEnabled));
            _setSheet(MobileSheet.ConfirmPairServer);
        });
        UnpairServerCommand = new RelayCommand(() =>
        {
            _sheetBeforeUnpair = _getSheet();
            OnPropertyChanged(nameof(ConfirmUnpairServerTitle));
            OnPropertyChanged(nameof(ConfirmUnpairServerMessage));
            _setSheet(MobileSheet.ConfirmUnpairServer);
        });
        ConfirmUnpairServerCommand = new RelayCommand(() =>
        {
            _main.UnpairServer();
            _setSheet(_sheetBeforeUnpair);
        });
        CancelUnpairServerCommand = new RelayCommand(() => _setSheet(_sheetBeforeUnpair));
        InviteDeviceCommand = new RelayCommand(async () => await _main.InviteDeviceToPairedServerAsync());
        AddManualServerCommand = new RelayCommand(async () =>
        {
            var address = ManualServerAddress.Trim();
            if (address.Length == 0)
                return;

            ManualServerStatus = "Looking for a server...";
            var found = await _main.AddManualServerAsync(address) != null;

            // A typo is by far the likeliest reason this fails, and finding
            // that out here beats finding it out from a coffee shop. The entry
            // is kept either way - a server that is merely switched off right
            // now is still the server the user meant.
            ManualServerStatus = found
                ? ""
                : $"Nothing answered at {address}. It is saved anyway - check the address, and that Tailscale is on at both ends.";
            if (found)
                ManualServerAddress = "";
        });
        RemoveManualServerCommand = new RelayCommand<string>(address =>
        {
            if (address != null)
                _main.RemoveManualServer(address);
        });
        ConfirmPairServerCommand = new RelayCommand(() =>
        {
            if (_pendingServerToPair is not { } device)
            {
                _setSheet(_sheetBeforePairing);
                return;
            }

            _main.PairingCodeError = null;

            // A code is accepted or refused within one round trip, and a
            // refusal has to land where the user is looking - next to the box
            // that produced it, with the typed code still there to correct. So
            // the sheet holds itself open until Settle sees one or the other.
            IsPairingInProgress = true;
            _main.PairWithServer(device, PendingPairingCode.Trim());
        });
        CancelPairServerCommand = new RelayCommand(() =>
        {
            _pendingServerToPair = null;
            PendingPairingCode = "";
            IsPairingInProgress = false;
            _main.PairingCodeError = null;
            _setSheet(_sheetBeforePairing);
        });
    }

    public ICommand PairWithServerCommand { get; }
    public ICommand UnpairServerCommand { get; }
    public ICommand ConfirmUnpairServerCommand { get; }
    public ICommand CancelUnpairServerCommand { get; }

    // Hands out a one-time pairing code for somebody else's device, if this
    // phone is an administrator of the server it is paired with - desktop's
    // equivalent is the "Generate Pairing Code" button in the sidebar's
    // device-detail header. See MainViewModel.InviteDeviceToPairedServerAsync.
    public ICommand InviteDeviceCommand { get; }

    // Bootstrap-only: an address for a server this phone has never shared a
    // network with, and therefore could never discover. A server paired with
    // at home reports its own addresses and needs none of this - see
    // docs/REMOTE-ACCESS.md.
    public ICommand AddManualServerCommand { get; }
    public ICommand RemoveManualServerCommand { get; }
    public ICommand ConfirmPairServerCommand { get; }
    public ICommand CancelPairServerCommand { get; }

    // ── Unpairing ────────────────────────────────────────────────────────
    //
    // Asked about rather than done on the tap, the way desktop's two Unpair
    // buttons ask: it removes every song this phone has not downloaded, and
    // getting them back takes a fresh code from the server's owner.

    private MobileSheet _sheetBeforeUnpair = MobileSheet.None;

    public string ConfirmUnpairServerTitle => $"Unpair From \"{_main.PairedServerAlias}\"?";
    public string ConfirmUnpairServerMessage => MainViewModel.UnpairConsequences(_main.PairedServerAlias ?? "this server");

    // ── Pairing ──────────────────────────────────────────────────────────

    // Set by PairWithServerCommand (Settings' server list) before raising the
    // sheet, cleared once it is confirmed or cancelled. Desktop's equivalent
    // is ServerPickerView's own ConfirmDialogWindow prompt.
    private DiscoveredDevice? _pendingServerToPair;
    public string PendingServerToPairAlias => _pendingServerToPair?.Alias ?? "";

    // Only one sheet is up at a time, so the pairing sheet takes Settings'
    // place rather than stacking on top of it, and whichever sheet it
    // displaced is put back when pairing resolves - otherwise finishing (or
    // cancelling) a pair drops the user all the way out to the library, when
    // what they did was tap one row inside Settings.
    private MobileSheet _sheetBeforePairing = MobileSheet.None;

    public string ConfirmPairServerTitle => $"Pair With \"{PendingServerToPairAlias}\"?";

    public string ConfirmPairServerActionLabel => IsPairingInProgress ? "Pairing..." : "Pair";

    public string ConfirmPairServerMessage =>
        $"This device's library view will be replaced by \"{PendingServerToPairAlias}\"'s - your Songs/Albums list will show its library instead of managing its own. Your existing music files on this device will not be deleted. "
        + "The pairing code below authorizes this device immediately.";

    // What the user typed into Settings' "add a server by address" box, and
    // the result of the last attempt. See AddManualServerCommand.
    private string _manualServerAddress = "";
    public string ManualServerAddress
    {
        get => _manualServerAddress;
        set => SetProperty(ref _manualServerAddress, value);
    }

    private string _manualServerStatus = "";
    public string ManualServerStatus
    {
        get => _manualServerStatus;
        set => SetProperty(ref _manualServerStatus, value);
    }

    // What the user typed into the sheet's code box. Reset every time the
    // sheet opens, so a code left from an abandoned attempt is never
    // submitted against a different server.
    private string _pendingPairingCode = "";
    public string PendingPairingCode
    {
        get => _pendingPairingCode;
        set
        {
            if (!SetProperty(ref _pendingPairingCode, value))
                return;
            OnPropertyChanged(nameof(IsConfirmPairServerEnabled));
            // Editing the code is the user retrying, and the message was about
            // the previous attempt - same rule as desktop's PairingCode setter.
            _main.PairingCodeError = null;
        }
    }

    // Set while a code is being redeemed, which is the whole time the sheet
    // holds itself open waiting for an answer.
    private bool _isPairingInProgress;
    public bool IsPairingInProgress
    {
        get => _isPairingInProgress;
        private set
        {
            if (!SetProperty(ref _isPairingInProgress, value))
                return;
            OnPropertyChanged(nameof(IsConfirmPairServerEnabled));
            OnPropertyChanged(nameof(ConfirmPairServerActionLabel));
        }
    }

    // "Pair" on an empty box would only round-trip to be rejected, and a
    // second tap while the first is still in flight would redeem the same
    // one-use code twice.
    public bool IsConfirmPairServerEnabled =>
        !IsPairingInProgress && !string.IsNullOrWhiteSpace(PendingPairingCode);

    // The other half of ConfirmPairServerCommand: the sheet stays up through
    // the redeem, so something has to take it back down. Trust confirmed means
    // the server accepted the code and the sheet is done; an error means it
    // stays, showing the reason, ready for another try. The shell calls this
    // when either of Main's two answers changes.
    public void Settle()
    {
        if (_getSheet() != MobileSheet.ConfirmPairServer || !IsPairingInProgress)
            return;

        if (_main.IsPairedServerTrustConfirmed)
        {
            IsPairingInProgress = false;
            _pendingServerToPair = null;
            PendingPairingCode = "";
            _setSheet(_sheetBeforePairing);
        }
        else if (!string.IsNullOrEmpty(_main.PairingCodeError))
        {
            IsPairingInProgress = false;
        }
    }
}
