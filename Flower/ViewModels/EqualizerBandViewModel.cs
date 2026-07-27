namespace Flower.ViewModels
{
    // One vertical slider's worth of state in the Equalizer window - backs
    // EqualizerViewModel.Bands. FrequencyLabel is precomputed display text
    // ("31", "16k") rather than a converter, since it never changes after
    // construction.
    public sealed class EqualizerBandViewModel : ViewModelBase
    {
        private readonly int _index;
        private readonly EqualizerViewModel _owner;

        public string FrequencyLabel { get; }

        public double GainDb
        {
            get => _owner.GetBandGainDb(_index);
            set
            {
                _owner.SetBandGainDb(_index, value);
                OnPropertyChanged();
            }
        }

        internal EqualizerBandViewModel(int index, double centerFreqHz, EqualizerViewModel owner)
        {
            _index = index;
            _owner = owner;
            FrequencyLabel = centerFreqHz >= 1000 ? $"{centerFreqHz / 1000:0.#}k" : $"{centerFreqHz:0}";
        }
    }
}
