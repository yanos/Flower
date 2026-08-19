using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Flower.Controls;

// Finding a ViewModel by walking up the visual tree to the nearest ancestor
// whose DataContext is one, rather than by asking the container for it.
//
// A control instantiated by XAML has no constructor to inject through, which
// is what left the Views/Controls layer service-locating its own ViewModels
// (docs/ARCHITECTURE-REVIEW.md Tier 2.3). The DataContext chain is the seam
// that was already there: whoever hosts this control has the ViewModel, and
// looking *up* for it costs nothing and needs no global. Prefer a plain
// DataContext or an event to the parent where either fits; this is for the
// case where a deeply nested control genuinely needs the screen's ViewModel
// and threading an event through every intermediate control would be worse.
public static class ViewTreeExtensions
{
    public static T? FindDataContext<T>(this Visual visual) where T : class =>
        visual.GetSelfAndVisualAncestors()
              .OfType<StyledElement>()
              .Select(element => element.DataContext)
              .OfType<T>()
              .FirstOrDefault();
}
