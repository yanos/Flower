using System;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Persistence;
using Flower.Services;

namespace Flower.Views;

public sealed record TrustedPeerRow(string Fingerprint, string Alias, DateTimeOffset ApprovedAt)
{
    public string ApprovedAtDisplay => $"Approved {ApprovedAt.LocalDateTime:g}";
}

// Lists peers approved via the trust gate (see SyncHttpServer.AuthorizeAsync,
// SYNC-PLAN.md Phase 3) and lets the user revoke one - the "forget this
// device" action the plan calls for. Opened from the app menu
// (MainWindow.axaml), alongside Rebuild Database/Open App Data Location -
// infrequent/administrative actions kept out of the already-decluttered
// Settings dialog rather than added back into it.
public partial class TrustedDevicesWindow : Window
{
    private readonly TrustedPeerStore _store = new();

    public TrustedDevicesWindow()
    {
        InitializeComponent();
        Refresh();
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    private void Refresh()
    {
        var rows = _store.Load()
            .OrderByDescending(p => p.ApprovedAt)
            .Select(p => new TrustedPeerRow(p.Fingerprint, p.Alias, p.ApprovedAt))
            .ToList();

        DevicesList.ItemsSource = rows;
        EmptyStateText.IsVisible = rows.Count == 0;
    }

    private async void ForgetButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TrustedPeerRow row })
            return;

        var confirmed = await ConfirmDialogWindow.ShowAsync(
            this,
            "Forget This Device?",
            $"\"{row.Alias}\" will need to be approved again before it can sync with this device.",
            "Forget");
        if (!confirmed)
            return;

        await _store.RevokeAsync(row.Fingerprint);
        Refresh();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
