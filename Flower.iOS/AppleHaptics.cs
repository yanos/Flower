using System;
using System.Linq;

using UIKit;

using Flower.Services;

namespace Flower.iOS;

// The tick UIKit's own context menus give as they open. Medium, as theirs is;
// the system's Haptics switch silences it.
public sealed class AppleHaptics : IHaptics
{
    // Made at the first long press rather than at launch: from iOS 17.5 a
    // generator belongs to a view, and there is no window yet when the
    // AppDelegate builds this. Left null, and asked for again, while there is
    // still no window to tie it to.
    private UIImpactFeedbackGenerator? _impact;

    public void LongPress() => (_impact ??= Create())?.ImpactOccurred();

    private static UIImpactFeedbackGenerator? Create()
    {
        if (!OperatingSystem.IsIOSVersionAtLeast(17, 5))
            return new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium);

        var window = UIApplication.SharedApplication.ConnectedScenes.ToArray()
            .OfType<UIWindowScene>()
            .Select(scene => scene.KeyWindow)
            .FirstOrDefault(key => key is not null);
        return window is null ? null : UIImpactFeedbackGenerator.GetFeedbackGenerator(UIImpactFeedbackStyle.Medium, window);
    }
}
