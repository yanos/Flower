using System.Collections.ObjectModel;
using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

using Flower.ViewModels;

namespace Flower.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly ObservableCollection<string> _paths;

    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _paths = new ObservableCollection<string>(viewModel.LibraryPaths);
        PathsList.ItemsSource = _paths;
    }

    private async void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider) return;

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a Music Folder",
            AllowMultiple = false,
        });

        if (folders.FirstOrDefault()?.TryGetLocalPath() is string path && !_paths.Contains(path))
            _paths.Add(path);
    }

    private void RemoveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (PathsList.SelectedItem is string path)
            _paths.Remove(path);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close();

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        await _viewModel.UpdateLibraryPathsAsync(_paths.ToList());
        Close();
    }
}
