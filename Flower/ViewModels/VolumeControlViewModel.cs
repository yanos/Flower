using Flower.Audio;

using Material.Icons;

namespace Flower.ViewModels
{
    public class VolumeControlViewModel : ViewModelBase
    {
        private readonly IAudioManager _audioManager;

        public int Volume
        {
            get => _audioManager.Volume;
            set
            {
                _audioManager.Volume = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Icon));
            }
        }

        // The speaker at the quiet end of the slider, saying how loud it is:
        // an X at nothing, no waves near the start, one in the middle, two
        // toward the top.
        public MaterialIconKind Icon => IconFor(Volume);

        public static MaterialIconKind IconFor(int volume) => volume switch
        {
            <= 0 => MaterialIconKind.VolumeMute,
            < 34 => MaterialIconKind.VolumeLow,
            < 67 => MaterialIconKind.VolumeMedium,
            _ => MaterialIconKind.VolumeHigh,
        };

        public VolumeControlViewModel(IAudioManager audioManager)
        {
            _audioManager = audioManager;
        }
    }
}
