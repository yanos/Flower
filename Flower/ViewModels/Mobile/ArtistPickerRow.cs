namespace Flower.ViewModels.Mobile;

// One row of mobile's Artists tab: the artist, and how many albums a tap on
// it opens onto (see MobileMainViewModel.RebuildArtistPickerRows).
public sealed record ArtistPickerRow(string Name, int AlbumCount)
{
    // What the row says, rather than a bare number beside a name that could
    // be counting anything.
    public string AlbumCountText => AlbumCount switch
    {
        0 => "No albums",
        1 => "1 album",
        _ => $"{AlbumCount} albums",
    };
}
