using System;

namespace Flower.Audio
{
    public static class VolumeCurve
    {
        // Maps the 0-100 slider onto the linear amplitude the output stage
        // ramps to. Cubic rather than the raw linear percent it used to be:
        // linear amplitude spends most of the slider's travel in a range the
        // ear barely separates, so the useful adjustment all happens in the
        // bottom fifth. This is roughly -18dB at half and -60dB at a tenth,
        // with no discontinuity at either end.
        public static float GainForPercent(int percent)
        {
            var normalized = Math.Clamp(percent, 0, 100) / 100f;
            return normalized * normalized * normalized;
        }

        // The slider's own volume moved by a track's adjustment
        // (IAudioManager.VolumeOffset), on the same 0-100 scale. Every
        // player asks this, then the curve above - native in its sink, the
        // browser in Web Audio - so the two cannot drift apart again.
        public static int EffectivePercent(int volume, int offset) => Math.Clamp(volume + offset, 0, 100);
    }
}
