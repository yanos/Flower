using Avalonia.Controls;

using Flower.Services;

namespace Flower.UserControls
{
    // Hosts the platform's own route picker, or nothing at all. There is no
    // ViewModel and no DataContext: the control has no state of Flower's to
    // show - the native view talks to the OS audio session directly, and the
    // route it picks applies to the whole process without Flower being told
    // to do anything about it.
    //
    // Resolved once, in the constructor, rather than bound: PlatformRoutePicker
    // .Current is set by the platform entry point before Avalonia starts and
    // never changes afterwards.
    public sealed class RoutePickerControl : ContentControl
    {
        public RoutePickerControl()
        {
            var picker = PlatformRoutePicker.Current?.CreatePicker();

            // Collapsed rather than merely blank on every platform without a
            // native picker, so the layout around it closes up instead of
            // leaving a hole where a button would be.
            IsVisible = picker != null;
            Content = picker;
        }
    }
}
