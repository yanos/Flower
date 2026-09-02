namespace Flower.Audio
{
    // One entry in the output-device picker. Id is an opaque, backend-specific
    // handle produced by whichever IAudioSink enumerated it - base64 of
    // miniaudio's ma_device_id union for MiniaudioSink - and is only ever
    // meaningful to the sink that handed it out. Nothing above IAudioSink
    // should parse or construct one; pass back exactly what GetOutputDevices
    // returned, or null for "whatever the OS default is".
    //
    // IsSystemDefault marks the device the OS currently considers default. It
    // is not the same as "the device Flower is using": selecting nothing
    // (IAudioManager.OutputDeviceId == null) means Flower follows the OS
    // default, which is a different state from having explicitly picked the
    // device that happens to be the default right now.
    public sealed record AudioOutputDevice(string Id, string Name, bool IsSystemDefault);
}
