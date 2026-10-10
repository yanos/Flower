namespace Flower.Controls;

/// <summary>
/// A phone screen that chooses where it opens, rather than at its top (a new
/// screen) or wherever it was left (one gone back to) - which is what
/// ScreenStackPanel does with every other screen. The Queue opens on the song
/// playing, with what has played above it, out of sight until scrolled to.
/// Asked once the screen is laid out where it will rest, every time it
/// becomes the screen showing.
/// </summary>
public interface IOpensAtItsOwnPlace
{
    void ScrollToItsPlace();
}
