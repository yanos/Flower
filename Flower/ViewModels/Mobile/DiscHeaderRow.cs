namespace Flower.ViewModels.Mobile;

// "Disc 2", between an album's songs on the phone's album screen, where the
// album has several (DiscHeaders) - an item of the list of its own, since a
// song's row is a button and the header is not part of any one song.
public sealed record DiscHeaderRow(string Title);
