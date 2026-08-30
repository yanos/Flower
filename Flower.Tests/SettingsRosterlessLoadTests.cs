using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Flower.Logging;
using Flower.ViewModels;

using Xunit;

namespace Flower.Tests;

// A backend with no trusted-device roster - this device's own settings, where
// LocalSettingsBackend throws NotSupportedException from every roster method
// because a client approves nobody.
//
// LoadAsync used to call LoadDevicesAsync regardless of Capabilities, catch that
// throw as an ordinary failure, and paint its message in the red error line that
// sits below the tabs - so opening this device's own Settings showed "This device
// has no trusted-device roster" under every pane. It also left IsLoaded false,
// which is what arms the iTunes master switch's folder side effect.
public class SettingsRosterlessLoadTests
{
    private sealed class RosterlessBackend : ISettingsBackend
    {
        public SettingsCapabilities Capabilities { get; } = new()
        {
            ThemePicker = true,
            ITunesIntegration = true,
            PairedServerPicker = true,
            RevealAppDataLocation = true,
            RebuildDatabase = true,
            TrustedDevices = false,
            PairingCodes = false,
            SubsonicCredentials = false,
            ServerNetwork = false,
            Log = false,
        };

        public Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default) =>
            Task.FromResult(new SettingsSnapshot { Alias = "Laptop" });

        public Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public int CountSongsUnder(string folder) => 0;

        private static NotSupportedException NoRoster() =>
            new("This device has no trusted-device roster - it does not accept incoming connections.");

        public Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) => throw NoRoster();
        public Task<IReadOnlyList<DeniedPeerRow>> LoadDeniedDevicesAsync(CancellationToken ct = default) => throw NoRoster();
        public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) => throw NoRoster();
        public Task ForgetDenialAsync(DeniedPeerRow device, CancellationToken ct = default) => throw NoRoster();

        public Task RescanAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RebuildDatabaseAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SubsonicCredentialRow>> LoadSubsonicCredentialsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SubsonicCredentialRow> IssueSubsonicCredentialAsync(string label, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RevokeSubsonicCredentialAsync(SubsonicCredentialRow credential, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<LogSlice> LoadLogAsync(int limit, long afterSequence, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InMemoryLogEntry>?> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Loading_a_backend_without_a_roster_reports_no_error()
    {
        var viewModel = new SettingsViewModel(new RosterlessBackend());

        await viewModel.LoadAsync();

        Assert.Null(viewModel.ErrorMessage);
        Assert.True(viewModel.IsLoaded);
        Assert.Equal("Laptop", viewModel.Alias);
    }
}
