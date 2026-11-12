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
    }
}
