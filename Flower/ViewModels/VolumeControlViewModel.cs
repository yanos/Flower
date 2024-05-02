using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Flower.Manager;

namespace Flower.ViewModels
{
    public class VolumeControlViewModel : ViewModelBase
    {
        private readonly IAudioManager _audioManager;

        public int Volume { get => _audioManager.Volume; set => _audioManager.Volume = value; }

        public VolumeControlViewModel(IAudioManager audioManager)
        {
            _audioManager = audioManager;
        }
    }
}
