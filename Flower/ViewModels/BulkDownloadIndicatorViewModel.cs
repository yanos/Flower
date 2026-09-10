namespace Flower.ViewModels;

// One icon standing for a whole screenful of tracks - mobile's top-bar
// "download all" (see MobileMainViewModel.DownloadAllIndicator).
//
// Everything it needs is the base class's: whether anything on the screen is
// still worth fetching, whether a batch is in flight, and the spinner's angle
// while it is. Nothing per-track to add, hence an otherwise empty subclass
// rather than making the base concrete - what kind of thing an indicator
// speaks for is exactly what its type says.
public sealed class BulkDownloadIndicatorViewModel : DownloadIndicatorViewModel;
