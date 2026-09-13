using System.Runtime.InteropServices;

using Miniaudio;

namespace Flower.Tests;

// MiniaudioSink's output-device enumeration walks a native ma_device_info
// array by C# struct stride, treats ma_device_id as an opaque fixed-size blob
// it base64s into AudioOutputDevice.Id, and reads the inline name at a fixed
// offset. All three only hold while Miniaudio-CS's generated structs still
// match the native miniaudio the app links against - and unlike ma_context,
// these have no ma_*_sizeof() escape hatch to ask the real library at runtime.
//
// The values below come straight from native/miniaudio/vendor/miniaudio.h
// (0.11.22, the commit Miniaudio-CS's bindings were generated against - see
// CLAUDE.md). Unlike ma_context, whose size varies with which backends a
// build enabled, these two are declared identically on every platform: every
// backend's member is present in the union regardless of what was compiled
// in. So a failure here means the binding and the header have drifted apart,
// which would show up at runtime as garbled device names or ids that select
// the wrong endpoint - not as a crash.
public class MiniaudioBindingLayoutTests
{
    [Fact]
    public void Device_id_is_still_an_opaque_256_byte_union()
    {
        Assert.Equal(256, Marshal.SizeOf<ma_device_id>());
    }

    [Fact]
    public void Device_info_still_has_the_layout_enumeration_walks()
    {
        // id, then char name[MA_MAX_DEVICE_NAME_LENGTH + 1], then isDefault,
        // nativeDataFormatCount and nativeDataFormats[64].
        Assert.Equal(0, (int)Marshal.OffsetOf<ma_device_info>(nameof(ma_device_info.id)));
        Assert.Equal(256, (int)Marshal.OffsetOf<ma_device_info>(nameof(ma_device_info.name)));
        Assert.Equal(256 + 256 + 4 + 4 + (64 * 16), Marshal.SizeOf<ma_device_info>());
    }
}
