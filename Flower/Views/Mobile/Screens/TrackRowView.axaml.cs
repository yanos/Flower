using System;

using Avalonia.Controls;

using Flower.Controls;
using Flower.ViewModels;

namespace Flower.Views.Mobile.Screens;

// One song's row on the phone. Everything the song decides is set here rather
// than bound, and the row follows the song through one ModelWatch - see that
// class for why.
public partial class TrackRowView : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly ModelWatch _row;

    public TrackRowView()
    {
        InitializeComponent();
        // The spinner turns every frame while a song downloads, and only the
        // download button draws it (it follows the row itself).
        _row = new ModelWatch(this, Show, ignoring: nameof(DownloadIndicatorViewModel.SpinAngle));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _row.Watch(DataContext as TrackRowViewModel);
    }

    private void Show()
    {
        if (DataContext is not TrackRowViewModel row)
            return;

        Classes.Set("playing", row.IsCurrentlyPlaying);
        Classes.Set("played", row.IsPlayed);
        Classes.Set("placeholder", row.IsUnavailable);

        CommandParameter = row;
        LongPress.SetCommandParameter(this, row);
        Download.CommandParameter = row;
        Menu.CommandParameter = row;

        DragGrip.IsVisible = row.CanBeDragged;
        Art.AlbumArt = row.AlbumArt;
        Title.Text = row.Track.Title;
        Artists.Text = row.Track.Artists;
        Duration.Text = row.DurationDisplay;
    }
}
