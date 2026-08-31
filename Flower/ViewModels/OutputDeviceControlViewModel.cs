using System.Collections.Generic;

using Flower.Manager;

namespace Flower.ViewModels
{
    // One row in the output picker. Id is the opaque token IAudioManager
    // handed out (see AudioOutputDevice), or null for the "System default"
    // row - the state where Flower follows the OS rather than pinning an
    // endpoint. Rebuilt wholesale on every Refresh instead of mutating in
    // place: the list is a handful of rows re-read each time the flyout
    // opens, so there is nothing to gain from change-tracking it.
    public sealed record OutputDeviceItemViewModel(string? Id, string Name, bool IsSelected);

    // Backs OutputDeviceControl, the speaker button beside the volume slider
    // in the desktop status bar. Deliberately thin: it re-enumerates on open
    // (there is no hotplug subscription anywhere in the audio stack) and
    // forwards the pick straight to IAudioManager.
    //
    // Desktop-only by construction rather than by a platform check - the
    // mobile shell has its own view tree (MobileMainView) and never hosts
    // this control, because iOS and Android already route all app audio
    // through whatever output the OS route picker selected. See
    // docs/AIRPLAY-BLUETOOTH-PLAN.md.
    public sealed class OutputDeviceControlViewModel : ViewModelBase
    {
        private const string SystemDefaultName = "System default";

        private readonly IAudioManager _audioManager;
        private IReadOnlyList<OutputDeviceItemViewModel> _devices = [];
        private bool _isAvailable;

        public OutputDeviceControlViewModel(IAudioManager audioManager)
        {
            _audioManager = audioManager;
            Refresh();
        }

        public IReadOnlyList<OutputDeviceItemViewModel> Devices
        {
            get => _devices;
            private set => SetProperty(ref _devices, value);
        }

        // Hides the whole control where routing is not a thing the app can
        // do: the browser build, or a sink that could not open a device at
        // all. A machine that really has exactly one output still shows the
        // picker - seeing which device is in use is worth the row.
        public bool IsAvailable
        {
            get => _isAvailable;
            private set => SetProperty(ref _isAvailable, value);
        }

        // Called every time the flyout opens, so a device plugged in since
        // the last look shows up without any hotplug plumbing.
        public void Refresh()
        {
            var selectedId = _audioManager.OutputDeviceId;
            var available = _audioManager.GetOutputDevices();

            var devices = new List<OutputDeviceItemViewModel>(available.Count + 1)
            {
                new(null, SystemDefaultName, selectedId == null)
            };

            foreach (var device in available)
                devices.Add(new OutputDeviceItemViewModel(device.Id, device.Name, device.Id == selectedId));

            Devices = devices;
            IsAvailable = available.Count > 0;
        }

        public void Select(OutputDeviceItemViewModel device)
        {
            _audioManager.SetOutputDevice(device.Id);

            // Re-read rather than assuming the pick stuck: SetOutputDevice
            // falls back to the system default if the chosen device vanished
            // between enumeration and here, and the tick has to follow.
            Refresh();
        }
    }
}
