using System.Collections.Generic;

using Flower.Audio;
using Flower.Persistence;

namespace Flower.ViewModels
{
    // Backs the Equalizer window (View > Equalizer...) - see LogViewModel
    // for the closest existing precedent (DI-singleton ViewModel, own
    // AppSettings persistence, window opened non-modally). Every mutation
    // here (Enabled/PreampDb/a band's GainDb) immediately rebuilds an
    // Equalizer and pushes it through IAudioManager.ApplyEqualizer - no
    // "Apply" button, live-apply. Disabling clears it (true bypass), rather
    // than pushing an all-zero-dB filter.
    public sealed class EqualizerViewModel : ViewModelBase
    {
        private readonly IAudioManager _audioManager;
        private readonly AppSettings _appSettings;
        private readonly AppSettingsStore _appSettingsStore;
        private readonly EqualizerSettings _settings;

        public IReadOnlyList<EqualizerBandViewModel> Bands { get; }

        public bool Enabled
        {
            get => _settings.Enabled;
            set
            {
                _settings.Enabled = value;
                OnPropertyChanged();
                ApplyAndSave();
            }
        }

        public double PreampDb
        {
            get => _settings.PreampDb;
            set
            {
                _settings.PreampDb = value;
                OnPropertyChanged();
                ApplyAndSave();
            }
        }

        public EqualizerViewModel(IAudioManager audioManager, AppSettings appSettings, AppSettingsStore appSettingsStore)
        {
            _audioManager = audioManager;
            _appSettings = appSettings;
            _appSettingsStore = appSettingsStore;

            // First-run fallback (never opened before) and a defensive
            // re-size against a hand-edited settings.json - both cases fall
            // back to a flat/disabled default rather than ever indexing
            // BandGainsDb out of range.
            var settings = appSettings.EqualizerSettings ?? new EqualizerSettings();
            if (settings.BandGainsDb.Length != Equalizer.BandCount)
                settings.BandGainsDb = new double[Equalizer.BandCount];
            appSettings.EqualizerSettings = settings;
            _settings = settings;

            var bands = new EqualizerBandViewModel[Equalizer.BandCount];
            for (var i = 0; i < Equalizer.BandCount; i++)
                bands[i] = new EqualizerBandViewModel(i, Equalizer.CenterFrequenciesHz[i], this);
            Bands = bands;
        }

        internal double GetBandGainDb(int index) => _settings.BandGainsDb[index];

        internal void SetBandGainDb(int index, double value)
        {
            _settings.BandGainsDb[index] = value;
            ApplyAndSave();
        }

        private void ApplyAndSave()
        {
            _audioManager.ApplyEqualizer(_settings.Enabled ? Equalizer.BuildFrom(_settings, GaplessFormat.SampleRate) : null);
            _ = _appSettingsStore.SaveAsync(_appSettings);
        }
    }
}
