using System;

using Avalonia.Controls;
using Avalonia.Headless.XUnit;

using Flower.Services;
using Flower.UserControls;

using Xunit;

namespace Flower.Tests;

// docs/AIRPLAY-BLUETOOTH-PLAN.md Phase 2: RoutePickerControl is the shared
// half of the AirPlay/Bluetooth button - the half that has to behave sanely
// on the four platforms that will never have a native picker to host.
//
// [AvaloniaFact] rather than [Fact] because ContentControl needs Avalonia's
// static services to exist before one can be constructed at all.
//
// PlatformRoutePicker.Current is process-wide static state set once at
// startup in production, so every test here restores whatever it found.
public class RoutePickerControlTests
{
    [AvaloniaFact]
    public void Hides_itself_when_the_platform_has_no_route_picker()
    {
        var previous = PlatformRoutePicker.Current;
        PlatformRoutePicker.Current = null;
        try
        {
            var control = new RoutePickerControl();

            Assert.False(control.IsVisible);
            Assert.Null(control.Content);
        }
        finally
        {
            PlatformRoutePicker.Current = previous;
        }
    }

    [AvaloniaFact]
    public void Hosts_the_platform_picker_when_there_is_one()
    {
        var previous = PlatformRoutePicker.Current;
        var native = new Border();
        PlatformRoutePicker.Current = new StubRoutePicker(native);
        try
        {
            var control = new RoutePickerControl();

            Assert.True(control.IsVisible);
            Assert.Same(native, control.Content);
        }
        finally
        {
            PlatformRoutePicker.Current = previous;
        }
    }

    // Stands in for Flower.iOS's AppleRoutePicker, whose CreatePicker returns
    // a NativeControlHost wrapping a real AVRoutePickerView.
    private sealed class StubRoutePicker(Control picker) : IPlatformRoutePicker
    {
        public Control CreatePicker() => picker;
    }
}
