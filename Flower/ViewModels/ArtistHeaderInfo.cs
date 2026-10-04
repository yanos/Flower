namespace Flower.ViewModels;

/// <summary>
/// What heads an artist's songs on the desktop Artists page
/// (MainViewModel.ArtistHeader): their name, their covers as a collage, and
/// how much of theirs there is.
/// </summary>
public sealed record ArtistHeaderInfo(string Name, AlbumCollage Cover, string Summary);
