using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Avalonia.Headless.XUnit;
using Avalonia.Threading;

using Flower.Audio;
using Flower.Models;
using Flower.Persistence;
using Flower.Services;
using Flower.Tests.TestSupport;
using Flower.ViewModels;

using Material.Icons;

using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Flower.Tests.ViewModels;

[Collection("PlatformDataDirectory")]
public class EqualizerViewModelTests : PinnedDataDirectory
{
    private static EqualizerViewModel Make(out FakeAudioManager audio, AppSettings? settings = null)
    {
        audio = new FakeAudioManager();
        return new EqualizerViewModel(audio, settings ?? new AppSettings(),
                                      new AppSettingsStore(NullLogger<AppSettingsStore>.Instance));
    }

    // Reset flattens the curve and the preamp, applies that once, and leaves
    // the equalizer on or off as it was.
    [Fact]
    public void Reset_puts_every_band_and_the_preamp_back_to_zero()
    {
        var vm = Make(out var audio);
        vm.Enabled = true;
        vm.PreampDb = -3;
        vm.Bands[2].GainDb = 4;
        vm.Bands[7].GainDb = -6;
        var raised = new List<string?>();
        vm.Bands[2].PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var before = audio.LastAppliedEqualizer;

        vm.Reset();

        Assert.True(vm.Enabled);
        Assert.Equal(0, vm.PreampDb);
        Assert.All(vm.Bands, b => Assert.Equal(0, b.GainDb));
        Assert.Contains(nameof(EqualizerBandViewModel.GainDb), raised);
        Assert.NotNull(audio.LastAppliedEqualizer);
        Assert.NotSame(before, audio.LastAppliedEqualizer);
    }

    [Fact]
    public void It_builds_one_band_per_equalizer_band()
    {
        var vm = Make(out _);

        Assert.Equal(Equalizer.BandCount, vm.Bands.Count);
    }

    // First run: no EqualizerSettings at all. The ViewModel installs a flat,
    // disabled default onto AppSettings rather than leaving it null.
    [Fact]
    public void First_run_installs_flat_disabled_defaults_onto_AppSettings()
    {
        var settings = new AppSettings { EqualizerSettings = null };
        var vm = Make(out _, settings);

        Assert.NotNull(settings.EqualizerSettings);
        Assert.False(vm.Enabled);
        Assert.Equal(Equalizer.BandCount, settings.EqualizerSettings!.BandGainsDb.Length);
        Assert.All(vm.Bands, b => Assert.Equal(0, b.GainDb));
    }

    // Defensive re-size against a hand-edited settings.json - the alternative
    // is indexing BandGainsDb out of range on the first slider move.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(64)]
    public void A_wrong_length_band_array_is_replaced_rather_than_indexed_out_of_range(int length)
    {
        var settings = new AppSettings
        {
            EqualizerSettings = new EqualizerSettings { BandGainsDb = new double[length] },
        };
        var vm = Make(out _, settings);

        Assert.Equal(Equalizer.BandCount, settings.EqualizerSettings!.BandGainsDb.Length);
        vm.Bands[Equalizer.BandCount - 1].GainDb = 6; // would throw if the array were still short
        Assert.Equal(6, vm.Bands[Equalizer.BandCount - 1].GainDb);
    }

    [Fact]
    public void Saved_settings_are_restored_rather_than_reset()
    {
        var gains = new double[Equalizer.BandCount];
        gains[4] = -7.5;
        var settings = new AppSettings
        {
            EqualizerSettings = new EqualizerSettings { Enabled = true, PreampDb = 3, BandGainsDb = gains },
        };
        var vm = Make(out _, settings);

        Assert.True(vm.Enabled);
        Assert.Equal(3, vm.PreampDb);
        Assert.Equal(-7.5, vm.Bands[4].GainDb);
    }

    // Live-apply: there is no Apply button, so every mutation has to push an
    // Equalizer through immediately.
    [Fact]
    public void Enabling_pushes_an_equalizer_and_disabling_clears_it()
    {
        var vm = Make(out var audio);

        vm.Enabled = true;
        Assert.NotNull(audio.LastAppliedEqualizer);

        // True bypass, not an all-zero-dB filter still in the signal path.
        vm.Enabled = false;
        Assert.Null(audio.LastAppliedEqualizer);
    }

    [Fact]
    public void Changing_a_band_or_the_preamp_while_enabled_pushes_a_rebuilt_equalizer()
    {
        var vm = Make(out var audio);
        vm.Enabled = true;
        var first = audio.LastAppliedEqualizer;

        vm.Bands[2].GainDb = 4;
        var afterBand = audio.LastAppliedEqualizer;
        Assert.NotSame(first, afterBand);

        vm.PreampDb = -2;
        Assert.NotSame(afterBand, audio.LastAppliedEqualizer);
    }

    // A band moved while the EQ is off must still be remembered, and must not
    // sneak an equalizer back into the signal path.
    [Fact]
    public void Changing_a_band_while_disabled_is_remembered_but_applies_nothing()
    {
        var vm = Make(out var audio);

        vm.Bands[1].GainDb = 5;

        Assert.Equal(5, vm.Bands[1].GainDb);
        Assert.Null(audio.LastAppliedEqualizer);
    }

    [Fact]
    public void A_band_reads_and_writes_only_its_own_slot()
    {
        var vm = Make(out _);

        vm.Bands[3].GainDb = 9;

        Assert.Equal(9, vm.Bands[3].GainDb);
        Assert.All(vm.Bands.Where((_, i) => i != 3), b => Assert.Equal(0, b.GainDb));
    }
}
