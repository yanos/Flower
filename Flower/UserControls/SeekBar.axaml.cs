using Avalonia.Controls;

namespace Flower.UserControls
{
    // DataContext is supplied by whoever hosts this control - a
    // CurrentlyPlayingControlViewModel, i.e. MainViewModel.NowPlaying.
    public partial class SeekBar : UserControl
    {
        public SeekBar()
        {
            InitializeComponent();
        }
    }
}
