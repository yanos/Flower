using AVFoundation;

using Microsoft.Extensions.Logging;

using Flower.Logging;
using Flower.Manager;

namespace Flower.iOS;

// Own the process-wide AVAudioSession only while Flower has audio to render.
// Playback is intentionally non-mixing: Flower's music should replace another
// music app only after the user explicitly presses Play. The playback category
// supports AirPlay and, with AllowBluetoothA2DP, stereo Bluetooth output such
// as AirPods rather than falling back to the handset speaker.
public sealed class AppleAudioSession : IPlatformAudioSession
{
    private readonly AVAudioSession _session = AVAudioSession.SharedInstance();

    public void ActivateForPlayback()
    {
        var categoryOptions = AVAudioSessionCategoryOptions.AllowBluetoothA2DP;
        if (!_session.SetCategory(AVAudioSessionCategory.Playback.GetConstant()!, categoryOptions, out var categoryError))
        {
            AppLogging.CreateLogger("Flower.iOS.AudioSession")
                .LogWarning("Could not configure the iOS playback audio session: {Error}", categoryError);
            return;
        }

        if (!_session.SetActive(true, out var activationError))
            AppLogging.CreateLogger("Flower.iOS.AudioSession")
                .LogWarning("Could not activate the iOS playback audio session: {Error}", activationError);
    }

    public void DeactivateAfterPlayback()
    {
        // Tell a player Flower interrupted (e.g. Music) that it may resume.
        if (!_session.SetActive(false, AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var deactivationError))
            AppLogging.CreateLogger("Flower.iOS.AudioSession")
                .LogWarning("Could not deactivate the iOS playback audio session: {Error}", deactivationError);
    }
}
