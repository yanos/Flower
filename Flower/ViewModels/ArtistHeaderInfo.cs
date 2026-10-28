namespace Flower.ViewModels;

/// <summary>
/// What heads an artist's songs on the desktop Artists page
/// (ArtistPageViewModel.Header): their name, their picture or their covers as
/// a collage (AlbumCollage.ForArtist), and how much of theirs there is.
/// </summary>
public sealed record ArtistHeaderInfo(string Name, AlbumCollage Cover, string Summary);
