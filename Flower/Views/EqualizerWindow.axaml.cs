using Avalonia.Controls;

using Flower.ViewModels;

namespace Flower.Views;

public partial class EqualizerWindow : Window
{
    // Satisfies Avalonia's runtime-XAML-loader/previewer check (AVLN3001) -
    // never called directly; the real constructor below is what's actually used.
    public EqualizerWindow() => InitializeComponent();

    public EqualizerWindow(EqualizerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
