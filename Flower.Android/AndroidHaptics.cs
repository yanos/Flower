using Android.App;
using Android.Views;

using Flower.Services;

namespace Flower.Android;

// Android's own long-press feedback, through the window's view - which needs
// no VIBRATE permission and follows the system's touch feedback setting.
public sealed class AndroidHaptics(Activity activity) : IHaptics
{
    public void LongPress() =>
        activity.Window?.DecorView.PerformHapticFeedback(FeedbackConstants.LongPress);
}
