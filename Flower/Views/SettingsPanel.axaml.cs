using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using Flower.ViewModels;

namespace Flower.Views;

// Code-behind for the shared settings screen. What is here rather than in
// SettingsViewModel is exactly what needs a view: the folder picker (which needs a
// TopLevel's StorageProvider), revealing a folder in the OS file manager, the
// focus dance around renaming a device row, and hosting ServerPickerView, which
// takes a MainViewModel and so cannot be declared in XAML.
public partial class SettingsPanel : UserControl
{
    private SettingsViewModel _viewModel = null!;

    // Only present when this panel is showing *this device's* settings - the
    // browser administering a remote server has no MainViewModel worth speaking of
    // and never shows the server picker.
    private MainViewModel? _mainViewModel;

    // Raised when the user is finished, on OK or Cancel alike. The host decides
    // what "close" means: SettingsWindow closes the window, the browser hides its
    // full-page overlay.
    public event EventHandler? CloseRequested;

    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) - never
    // called directly; Initialize below is what actually wires it up.
    public SettingsPanel() => InitializeComponent();

    public SettingsPanel(SettingsViewModel viewModel, MainViewModel? mainViewModel = null) : this() =>
        Initialize(viewModel, mainViewModel);

    public void Initialize(SettingsViewModel viewModel, MainViewModel? mainViewModel = null)
    {
        _viewModel = viewModel;
        _mainViewModel = mainViewModel;
        DataContext = viewModel;

        viewModel.DeviceListChanged += OnDeviceListChanged;
        RefreshDevicesTab();

        // Back to the tab this screen was left on. Before anything else that
        // reacts to the selection, so the log poller and the rest see the tab
        // the reader is actually going to be looking at rather than General
        // for a moment first.
        RestoreRememberedTab();

        // The Logs tab follows a live log while it is the tab on screen (see
        // SettingsViewModel.SetLogTabActive). Coming and going matters as much
        // as the tab selection does: this panel outlives being looked at - the
        // server pane keeps it paired with its ViewModel rather than rebuilding
        // both (MainView._serverSettingsPanels) - so it stops polling a server
        // nobody is watching when it leaves the screen and picks it up again
        // when it comes back.
        Loaded += OnPanelLoaded;
        Unloaded += (_, _) => _viewModel.SetLogTabActive(false);

        if (_mainViewModel != null)
        {
            // Pairing/unpairing happens inside ServerPickerView, not through
            // anything this panel owns directly - listen for it so the Library
            // tab's enabled state stays live if the user pairs or unpairs while
            // Settings is still open, not just at construction time.
            _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
            Unloaded += (_, _) => _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        }

        _ = viewModel.LoadAsync();
    }

    // Whether this panel has been on screen before. The first Loaded follows
    // the load Initialize has already started; every one after it is the panel
    // coming back, which is where it has to catch up on whatever happened while
    // it was away - a device paired from a phone, a folder added from the
    // server's own browser UI.
    private bool _hasBeenShown;

    private void OnPanelLoaded(object? sender, RoutedEventArgs e)
    {
        if (_hasBeenShown)
            _ = _viewModel.LoadAsync();
        _hasBeenShown = true;

        SyncLogTabActive();
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PairedServerFingerprint))
            _ = _viewModel.LoadAsync();
    }

    private void OnDeviceListChanged(object? sender, EventArgs e) => RefreshDevicesTab();

    // Fires once during XAML load, before Initialize, with no ViewModel to tell
    // - hence the null checks rather than an assumption that this only runs
    // after wiring.
    private void Tabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel == null)
            return;

        if (Tabs.SelectedItem is TabItem { Name: { } name })
            _viewModel.RememberedTab = name;

        SyncLogTabActive();
    }

    // A remembered name that no longer names a visible tab is ignored, not
    // corrected: the same person administers a server from a browser and their
    // own app from the desktop, capabilities differ between the two, and
    // "Network" is not somewhere the local screen can land.
    private void RestoreRememberedTab()
    {
        var remembered = Tabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(tab => tab.IsVisible && tab.Name == _viewModel.RememberedTab);
        if (remembered != null)
            Tabs.SelectedItem = remembered;
    }

    private void SyncLogTabActive() =>
        _viewModel?.SetLogTabActive(ReferenceEquals(Tabs.SelectedItem, LogsTab));

    // Which half of the Devices tab this backend gets: a device configuring
    // itself picks the one server it syncs with, and a server being configured
    // shows the roster of devices allowed to sync with it. Never both, and -
    // unlike when either end could be either thing - never a choice that can
    // change while the panel is open.
    private void RefreshDevicesTab()
    {
        var showsPicker = _mainViewModel != null && _viewModel.Capabilities.PairedServerPicker;

        ServerPickerHost.IsVisible = showsPicker;
        ServerPickerHost.Content = showsPicker ? new ServerPickerView(_mainViewModel!) : null;

        TrustedDevicesSection.IsVisible = _viewModel.Capabilities.TrustedDevices;
    }

    private async void AddFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
            return;

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a Music Folder",
            AllowMultiple = false,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is string path)
            _viewModel.AddLibraryPath(path);
    }

    // Mirrors MainViewModel.OpenAppDataLocation's per-OS reveal-in-file-manager
    // logic - opens the folder itself rather than selecting it within its parent,
    // since these are the library folders themselves, not files.
    private void RevealFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        // A browser tab cannot open a file manager, and the path it is showing is
        // very likely on another machine entirely.
        if (OperatingSystem.IsBrowser())
            return;
        if ((sender as Button)?.DataContext is not LibraryPathRow row)
            return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { row.Path } });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { row.Path } });
        else
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { row.Path } });
    }

    // The addresses are shown so they can be typed into another device, and a
    // phone at the other end of the room is not going to be typed into from a
    // selection: copying is the actual gesture. TopLevel rather than a service -
    // this panel is hosted both by a window and, in the browser, full-page.
    private void CopyAddressButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ServerAddressRow row)
            return;

        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(row.Address);
    }

    private void OpenAppDataLocationButton_Click(object? sender, RoutedEventArgs e) =>
        _mainViewModel?.OpenAppDataLocationCommand?.Execute(null);

    private void ForgetButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is TrustedPeerRow row)
            row.IsConfirmingForget = true;
    }

    private void CancelForgetButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is TrustedPeerRow row)
            row.IsConfirmingForget = false;
    }

    private void ConfirmForgetButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is TrustedPeerRow row)
            _viewModel.ForgetDeviceCommand.Execute(row);
    }

    private void ForgetRefusalButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is DeniedPeerRow row)
            _viewModel.ForgetDenialCommand.Execute(row);
    }

    // Hosted inside another page rather than in a window or a full-page overlay
    // (MainView's device-detail pane): there is nothing to close, so Cancel -
    // which only ever meant "close without saving" - has nothing to mean either,
    // and the button that saves says so instead of saying OK.
    public void UseInlineChrome()
    {
        CancelButton.IsVisible = false;
        SaveButton.Content = "Save";
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    // Closes as soon as the (fast) settings write lands rather than waiting for the
    // (potentially long) library rescan, which the backend deliberately leaves
    // running - see LocalSettingsBackend.SaveAsync. A failed save keeps the panel
    // open with the reason showing, since closing would discard the edits with no
    // sign anything went wrong.
    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (await _viewModel.SaveAsync())
            CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
