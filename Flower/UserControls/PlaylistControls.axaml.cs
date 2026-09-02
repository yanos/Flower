using System;

using Avalonia.Controls;
using Avalonia.Data.Converters;

using Flower.Audio;
using Flower.ViewModels;

namespace Flower.UserControls
{
    // DataContext is supplied by whoever hosts this control - MainView.axaml
    // binds it to MainViewModel.PlaybackControls. Nothing here reaches into the
    // container: see docs/ARCHITECTURE-REVIEW.md Tier 2.3.
    public partial class PlaylistControls : UserControl
    {
        public PlaylistControls()
        {
            InitializeComponent();
        }

        private PlaylistControlViewModel? ViewModel => DataContext as PlaylistControlViewModel;

        // Play/pause deliberately does *not* go through the ViewModel this
        // control is bound to: a fresh play (nothing currently playing or
        // paused) has to snapshot the queue from whatever MainView is
        // currently displaying - see MainViewModel.PlayOrPauseFromCurrentView.
        // Raised as an event rather than resolved as a second ViewModel, so
        // this control still knows about exactly one of them.
        public event EventHandler? PlayOrPauseRequested;

        private void PlayOrPause(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            PlayOrPauseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void Next(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ViewModel?.Next();
        }

        private void Previous(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            ViewModel?.Previous();
        }
    }
}
