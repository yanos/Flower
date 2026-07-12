using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using Flower.Importer;
using Flower.Persistence;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

// One row in SettingsWindow's library-paths list - SongCountDisplay is how
// many of the library's current tracks live under Path, so a user can tell
// at a glance whether a folder actually contributed anything to the scan.
public sealed record LibraryPathRow(string Path, int SongCount)
{
    public string SongCountDisplay => SongCount == 1 ? "1 song" : $"{SongCount:N0} songs";
}

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly List<string> _paths;
    private readonly List<string> _originalPaths;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _paths = new List<string>(viewModel.LibraryPaths);
        _originalPaths = new List<string>(_paths);
        RefreshPathRows();
        DeviceAliasTextBox.Text = viewModel.DeviceAlias;
        SyncPlayCountCheckBox.IsChecked = viewModel.SyncPlayCountFromITunes;
        ITunesLibraryPathText.Text = DescribeITunesLibrarySource();
        ThemeComboBox.SelectedIndex = viewModel.ThemePreference switch
        {
            AppThemePreference.Light => 1,
            AppThemePreference.Dark => 2,
            _ => 0,
        };
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    // Describes where play counts will actually come from without doing any
    // slow work itself (the live export - see ITunesPlayCountImporter - isn't
    // triggered just to populate this label; only checking whether Music.app
    // is installed at all, and whether a fallback file exists).
    private static string DescribeITunesLibrarySource()
    {
        if (Directory.Exists("/System/Applications/Music.app") || Directory.Exists("/Applications/Music.app"))
            return "Exports a fresh copy from Music.app each launch";

        return ITunesPlayCountImporter.ResolveLibraryXmlPath() is string fallbackPath
            ? $"Music.app not found - using {fallbackPath}"
            : "No iTunes/Music library data available";
    }

    private void RefreshPathRows()
    {
        PathsList.ItemsSource = _paths
            .Select(path => new LibraryPathRow(path, CountSongsUnder(path)))
            .ToList();
    }

    private int CountSongsUnder(string folder)
    {
        var prefix = folder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;
        return _viewModel.Library.Tracks.Count(t => t.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private async void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
            return;

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a Music Folder",
            AllowMultiple = false,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is string path && !_paths.Contains(path))
        {
            _paths.Add(path);
            RefreshPathRows();
        }
    }

    private void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (PathsList.SelectedItem is LibraryPathRow row)
        {
            _paths.Remove(row.Path);
            RefreshPathRows();
        }
    }

    // Moved here from the old top-level "Library" menu (MainWindow.axaml),
    // along with Rebuild Database (same tab) and Trusted Devices (its own
    // Devices tab, below) - that menu is gone now that Settings is where all
    // three live instead.
    private void OpenAppDataLocationButton_Click(object? sender, RoutedEventArgs e) =>
        _viewModel.OpenAppDataLocationCommand?.Execute(null);

    private void RebuildDatabaseButton_Click(object? sender, RoutedEventArgs e) =>
        _viewModel.RebuildDatabaseCommand?.Execute(null);

    // Opens TrustedDevicesWindow owned by MainWindow (see MainView.axaml.cs's
    // OnTrustedDevicesRequested), not by this Settings window - it and
    // Settings end up as two independent windows open at once rather than a
    // strict parent/child pair, but nothing about that command cares which
    // window triggered it, so reusing it as-is here is simplest.
    private void TrustedDevicesButton_Click(object? sender, RoutedEventArgs e) =>
        _viewModel.OpenTrustedDevicesCommand?.Execute(null);

    // Mirrors MainViewModel.OpenAppDataLocation's per-OS reveal-in-file-manager
    // logic - opens the folder itself rather than selecting it within its
    // parent, since these are the library folders themselves, not files.
    private void RevealFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not LibraryPathRow row)
            return;
        var path = row.Path;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { path } });
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { path } });
        else
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { path } });
    }

    private void SyncPlayCountCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e) =>
        _viewModel.SyncPlayCountFromITunes = SyncPlayCountCheckBox.IsChecked ?? false;

    // Fires once during the constructor's own initial SelectedIndex set too -
    // harmless, since MainViewModel.ThemePreference's setter already no-ops
    // when the value hasn't actually changed.
    private void ThemeComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _viewModel.ThemePreference = ThemeComboBox.SelectedIndex switch
        {
            1 => AppThemePreference.Light,
            2 => AppThemePreference.Dark,
            _ => AppThemePreference.System,
        };

    // Applied on LostFocus (tabbing/clicking away), not per-keystroke - matches
    // the checkbox above's apply-immediately-on-change behavior without writing
    // to disk on every character typed. An empty entry is never persisted (would
    // show a blank name to peers); the textbox just reverts to the last real value.
    private void DeviceAliasTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        var trimmed = DeviceAliasTextBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(trimmed))
        {
            DeviceAliasTextBox.Text = _viewModel.DeviceAlias;
            return;
        }

        _viewModel.DeviceAlias = trimmed;
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    // Closes as soon as the (fast) path list is saved rather than waiting for
    // the (potentially long) library rescan - RescanLibraryAsync runs
    // unawaited afterward, with progress shown via MainView's existing busy
    // spinner (see MainViewModel.RebuildDatabaseAsync's BeginBusy). Only
    // actually saves/rescans if a folder was added or removed - reordering
    // never happens (Add always appends, Remove just removes), so a plain
    // set comparison against the paths this dialog opened with is enough.
    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        var pathsChanged = !_paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(_originalPaths);
        if (pathsChanged)
            await _viewModel.SaveLibraryPathsAsync(_paths.ToList());

        Close();

        if (pathsChanged)
            _ = _viewModel.RescanLibraryAsync();
    }
}
