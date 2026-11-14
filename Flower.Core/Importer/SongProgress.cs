namespace Flower.Importer;

// How far a run over a known set of songs has got: Done of Total, Done
// counting the songs finished (read, copied) rather than the one under way.
// Reported by whatever walks a library's files one at a time - a scan
// (Importer), songs dropped on the window (LibraryDrop, then
// LibraryFolderWatcher.AddFilesAsync) - for the status bar to say how many are
// left, a scan of a big library on a network drive being minutes of "Refreshing
// Library" otherwise.
public readonly record struct SongProgress(int Done, int Total);
