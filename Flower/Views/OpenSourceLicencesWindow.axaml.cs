using Avalonia.Controls;
using Avalonia.Input;

using Flower.Services;

namespace Flower.Views;

// NOTICE and the licence texts, opened from the About window - see
// OpenSourceNotices.
public partial class OpenSourceLicencesWindow : Window
{
    public OpenSourceLicencesWindow()
    {
        InitializeComponent();
        NoticesTextBlock.Text = OpenSourceNotices.Text;
    }

    // Read-only, like AboutWindow: Escape closes it.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
            Close();
    }
}
