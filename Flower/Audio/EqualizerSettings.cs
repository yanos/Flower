namespace Flower.Audio
{
    // Persisted EQ configuration - see Equalizer for the DSP that consumes
    // this. BandGainsDb is fixed at Equalizer.BandCount (10) entries, one per
    // Equalizer.CenterFrequenciesHz - not a per-instance-configurable band
    // count (no parametric/preset support, per AUDIOPHILE-PLAN.md).
    public sealed class EqualizerSettings
    {
        public bool Enabled { get; set; }
        public double PreampDb { get; set; }
        public double[] BandGainsDb { get; set; } = new double[Equalizer.BandCount];
    }
}
