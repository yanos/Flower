using UIKit;

using Flower.Services;

namespace Flower.iOS;

// The tick UIKit's own context menus give as they open. Medium, as theirs is;
// the system's Haptics switch silences it.
public sealed class AppleHaptics : IHaptics
{
    private readonly UIImpactFeedbackGenerator _impact = new(UIImpactFeedbackStyle.Medium);

    public void LongPress() => _impact.ImpactOccurred();
}
