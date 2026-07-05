using System.Linq;

using Avalonia.Controls;
using Avalonia.Interactivity;

using Flower.Controls;
using Flower.Services;
using Flower.ViewModels;

namespace Flower.Views;

public partial class ColumnSelectorWindow : Window
{
    // Required by the Avalonia XAML compiler (the {Binding} inside the
    // DataTemplate below needs to validate against a constructible instance);
    // never actually used by app code, which always goes through the
    // constructor below.
    public ColumnSelectorWindow()
    {
        InitializeComponent();
    }

    public ColumnSelectorWindow(ColumnManager columnManager, MainViewModel viewModel)
    {
        InitializeComponent();
        ColumnsList.ItemsSource = columnManager.Columns.OrderBy(c => c.Order).ToList();
        DataContext = viewModel;
        NativeMenuHelper.InheritFromMainWindow(this);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
