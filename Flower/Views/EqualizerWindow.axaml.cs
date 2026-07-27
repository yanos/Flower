using Avalonia.Controls;

using Flower.ViewModels;

namespace Flower.Views;

public partial class EqualizerWindow : Window
{
    public EqualizerWindow(EqualizerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
