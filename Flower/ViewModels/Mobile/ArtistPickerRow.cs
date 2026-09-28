namespace Flower.ViewModels.Mobile;

// One row of mobile's Artists tab: the artist, how much of theirs there is,
// and a cover made of their most played albums' art (see AlbumCollage, and
// MobileMainViewModel.RebuildArtistPickerRows).
public sealed class ArtistPickerRow
{
    public ArtistPickerRow(string name, int albumCount, int songCount, AlbumCollage cover)
    {
        Name = name;
        AlbumCount = albumCount;
        SongCount = songCount;
        Cover = cover;
    }

    public string Name { get; }
    public int AlbumCount { get; }
    public int SongCount { get; }
    public AlbumCollage Cover { get; }

    // The line under the name, rather than a bare number beside it that could
    // be counting anything.
    public string SummaryText => Summary(AlbumCount, SongCount);

    public static string Summary(int albums, int songs)
    {
        var albumText = albums switch
        {
            0 => "No albums",
            1 => "1 album",
            _ => $"{albums} albums",
        };
        return $"{albumText} · {songs} {(songs == 1 ? "song" : "songs")}";
    }
}
