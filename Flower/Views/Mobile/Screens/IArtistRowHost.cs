using System.Windows.Input;

namespace Flower.Views.Mobile.Screens;

// Implemented by each phone screen that lists artists (ArtistRowView) - the
// Artists tab and Search - to say what tapping one does, which is the one
// thing about the row that differs between them: each opens the artist its
// own way, so Back returns to the screen it was opened from. Run with the
// artist's name.
public interface IArtistRowHost
{
    ICommand? OpenArtistCommand { get; }
}
