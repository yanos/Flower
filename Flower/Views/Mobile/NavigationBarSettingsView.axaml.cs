using Avalonia.Controls;

using Flower.Controls;
using Flower.ViewModels.Mobile;

namespace Flower.Views.Mobile;

// The Navigation Bar page - see its XAML. What is here is the drag that
// reorders the bar's rows (HandleDragReorder): only a tab in the bar has a
// place to move, and a drop moves it through MobileMainViewModel.MoveTab.
public partial class NavigationBarSettingsView : UserControl
{
    public NavigationBarSettingsView()
    {
        InitializeComponent();

        _ = new HandleDragReorder(VisibleList,
            item => item is MobileTabSettingRow { IsShown: true } row && Vm is { } vm ? vm.VisibleTabRows.IndexOf(row) : -1,
            (item, to) => Vm?.MoveTab(((MobileTabSettingRow)item).Tab, to));
    }

    private MobileMainViewModel? Vm => DataContext as MobileMainViewModel;
}
