using System.Collections.Generic;
using System.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Flower.Controls;

namespace Flower.Tests.TestSupport;

// The words a view shows, wherever they are drawn. Most text is a TextBlock,
// but a phone track row's title, artist and duration are RowText (see that
// control for why), so a test looking for a song's title by TextBlock alone
// finds nothing on the very rows it is about.
internal static class ShownText
{
    public static string? Of(Visual visual) => visual switch
    {
        TextBlock text => text.Text,
        RowText text => text.Text,
        _ => null,
    };

    public static IEnumerable<Control> In(Visual root) =>
        root.GetVisualDescendants().OfType<Control>().Where(c => c is TextBlock or RowText);

    public static Control Saying(Visual root, string text) =>
        In(root).First(c => Of(c) == text && c.IsEffectivelyVisible);
}
