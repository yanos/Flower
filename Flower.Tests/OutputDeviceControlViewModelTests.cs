using System.Linq;

using Flower.Manager;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

namespace Flower.Tests;

// Covers the picker's own logic against FakeAudioManager: the synthetic
// "System default" row, which row carries the tick, and what happens when a
// device disappears between enumeration and selection. MiniaudioSink's actual
// ma_context/ma_device enumeration and reopen are not exercised here - they
// need real audio hardware, and are the manual-verification half of
// docs/AIRPLAY-BLUETOOTH-PLAN.md's Phase 1.
public class OutputDeviceControlViewModelTests
{
    private static FakeAudioManager WithDevices(params AudioOutputDevice[] devices) =>
        new() { OutputDevices = devices };

    private static AudioOutputDevice Device(string id, bool isDefault = false) =>
        new(id, $"Device {id}", isDefault);

    [Fact]
    public void Hides_itself_when_no_devices_are_reported()
    {
        var vm = new OutputDeviceControlViewModel(new FakeAudioManager());

        Assert.False(vm.IsAvailable);
    }

    [Fact]
    public void Lists_a_system_default_row_above_the_enumerated_devices()
    {
        var vm = new OutputDeviceControlViewModel(WithDevices(Device("a", isDefault: true), Device("b")));

        Assert.True(vm.IsAvailable);
        Assert.Equal(3, vm.Devices.Count);
        Assert.Null(vm.Devices[0].Id);
        Assert.Equal(["a", "b"], vm.Devices.Skip(1).Select(d => d.Id));
    }

    [Fact]
    public void Ticks_system_default_until_a_device_is_picked()
    {
        var audio = WithDevices(Device("a"), Device("b"));
        var vm = new OutputDeviceControlViewModel(audio);

        Assert.True(vm.Devices[0].IsSelected);

        vm.Select(vm.Devices.Single(d => d.Id == "b"));

        Assert.Equal("b", audio.OutputDeviceId);
        Assert.False(vm.Devices[0].IsSelected);
        Assert.True(vm.Devices.Single(d => d.Id == "b").IsSelected);
    }

    [Fact]
    public void Selecting_the_system_default_row_clears_the_pinned_device()
    {
        var audio = WithDevices(Device("a"));
        var vm = new OutputDeviceControlViewModel(audio);
        vm.Select(vm.Devices.Single(d => d.Id == "a"));

        vm.Select(vm.Devices[0]);

        Assert.Null(audio.OutputDeviceId);
        Assert.True(vm.Devices[0].IsSelected);
    }

    [Fact]
    public void Follows_the_manager_back_to_the_default_when_the_pick_does_not_stick()
    {
        // Standing in for a device unplugged between the flyout opening and
        // the click landing: the manager falls back to the system default,
        // and the tick has to end up there rather than on the row clicked.
        var audio = new FakeAudioManager { OutputDevices = [Device("a")] };
        var vm = new OutputDeviceControlViewModel(audio);
        var vanished = new OutputDeviceItemViewModel("gone", "Gone", false);

        vm.Select(vanished);

        Assert.Null(audio.OutputDeviceId);
        Assert.True(vm.Devices[0].IsSelected);
    }

    [Fact]
    public void Refresh_picks_up_a_device_that_appeared_since_the_last_look()
    {
        var audio = WithDevices(Device("a"));
        var vm = new OutputDeviceControlViewModel(audio);

        audio.OutputDevices = [Device("a"), Device("b")];
        vm.Refresh();

        Assert.Equal(["a", "b"], vm.Devices.Skip(1).Select(d => d.Id));
    }
}
