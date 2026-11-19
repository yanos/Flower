using Avalonia;

namespace Flower.Controls;

// The finger's half of a long press, apart from the timer and the element
// (LongPress): whether the touch that started the wait is still one that
// could be a long press when the wait is up.
//
// Avalonia's own Holding gesture gives up once the finger has drifted 4px
// from where it landed, and a finger resting on glass drifts further than
// that often enough - which was a long press that, now and then, did nothing.
// Slop is the 10 points iOS allows (Android's touch slop is 8dp): past it the
// finger is going somewhere, and is a scroll or nothing.
public sealed class LongPressTracker
{
    public const double Slop = 10;

    private object? _pointer;
    private Point _origin;

    public bool IsPending => _pointer is not null;

    // A touch lands, starting the wait - and ending any other's, since a
    // second finger down is not a long press of the first.
    public void Press(object pointer, Point at)
    {
        _pointer = pointer;
        _origin = at;
    }

    public void Move(object pointer, Point at)
    {
        if (!ReferenceEquals(pointer, _pointer))
            return;
        var dx = at.X - _origin.X;
        var dy = at.Y - _origin.Y;
        if (dx * dx + dy * dy > Slop * Slop)
            _pointer = null;
    }

    // Released, captured by something else, or otherwise gone.
    public void End(object pointer)
    {
        if (ReferenceEquals(pointer, _pointer))
            _pointer = null;
    }

    // The wait is up: true once, for a touch still resting where it landed.
    public bool Fire(object pointer)
    {
        if (!ReferenceEquals(pointer, _pointer))
            return false;
        _pointer = null;
        return true;
    }
}
