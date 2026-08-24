using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Flower.Persistence;
using Flower.Services;

namespace Flower.ViewModels;

// A Flower server's settings, over its admin API - the backend behind the
// settings page in the server's own web UI.
//
// Everything here is one call to ServerAdminClient; the interesting part is what
// is *absent*. There is no theme (a browser tab administering a headless box has
// no app theme to set), no Client/Server role choice (a server is one,
// unconditionally) and no "Rebuild Database" - FlowerDb migrates itself on
// construction, so a server has a rescan and nothing to rebuild.
//
// Music.app integration is *not* absent, though the browser has none of its own:
// the switches administer the machine the server runs on, which is very often a
// Mac with an existing Music.app library, and the server applies them in its own
// scan (LibraryImportService). When that machine has no Music.app library the
// server reports a null AppleMusicFolder and the page disables all three.
public sealed class RemoteServerSettingsBackend(ServerAdminClient client) : ISettingsBackend
{
    private ServerSettingsDto? _lastLoaded;

    public SettingsCapabilities Capabilities { get; } = new()
    {
        ServerNetwork = true,
        PairingCodes = true,
        SubsonicCredentials = true,
        Log = true,
        ThemePicker = false,
        ITunesIntegration = true,
        PairedServerPicker = false,
        TrustedDevices = true,
        RevealAppDataLocation = false,
        RebuildDatabase = false,
    };

    public async Task<SettingsSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var settings = await client.GetSettingsAsync(ct);
        _lastLoaded = settings;

        return new SettingsSnapshot
        {
            Alias = settings.Alias,
            LibraryPaths = settings.LibraryPaths,
            IntegrateWithITunes = settings.IntegrateWithITunes,
            SyncPlayCountFromITunes = settings.SyncPlayCountFromITunes,
            SyncDateAddedFromITunes = settings.SyncDateAddedFromITunes,
            AppleMusicFolder = settings.AppleMusicFolder,
            ITunesLibraryDescription = settings.ITunesLibraryDescription,
            AdvertisedHost = settings.AdvertisedHost,
            AdvertiseOnLan = settings.AdvertiseOnLan,
            TrustTailscaleRange = settings.TrustTailscaleRange,
            AllowedCidrs = settings.AllowedCidrs,
            DataDirectory = settings.DataDirectory,
            Version = settings.Version,
        };
    }

    public async Task<string?> SaveAsync(SettingsDraft draft, CancellationToken ct = default)
    {
        // Sent as one PUT rather than a call per field: the server writes them to
        // flower-server.json in a single read-modify-write, and half-applied
        // settings after a dropped connection is not a state worth being able to
        // reach.
        var result = await client.UpdateSettingsAsync(
            new ServerSettingsUpdateDto(
                draft.Alias,
                draft.AdvertisedHost,
                draft.AdvertiseOnLan,
                draft.TrustTailscaleRange,
                draft.AllowedCidrs.ToList(),
                draft.LibraryPaths.ToList(),
                draft.IntegrateWithITunes,
                draft.SyncPlayCountFromITunes,
                draft.SyncDateAddedFromITunes),
            ct);
        _lastLoaded = result;

        // A changed library folder is only half the job - the tracks under it are
        // not in the catalog until something scans them, and an operator who adds
        // a folder and sees nothing happen reasonably concludes it did not work.
        if (draft.LibraryPathsChanged)
            await client.RescanAsync(ct);

        if (result.RestartRequired is not { Count: > 0 } restart)
            return draft.LibraryPathsChanged ? "Saved. Scanning the library folders now." : null;

        // MdnsAdvertiser reads its options once, when the hosted service starts,
        // so these are on disk and bound but not yet announced. Said plainly
        // rather than silently: the alternative is an operator renaming a server
        // and watching the old name stay in every client's sidebar.
        return $"Saved. Restart the server to apply: {string.Join(", ", restart)}.";
    }

    // A server's catalog is not in this process, and attributing tracks to folders
    // would mean pulling the whole thing over the wire just to render a subtitle.
    public int CountSongsUnder(string folder) => -1;

    public async Task<IReadOnlyList<TrustedPeerRow>> LoadDevicesAsync(CancellationToken ct = default) =>
        (await client.GetDevicesAsync(ct))
            .OrderByDescending(d => d.ApprovedAt)
            .Select(d => new TrustedPeerRow
            {
                Fingerprint = d.Fingerprint,
                Alias = d.Alias,
                ApprovedAt = d.ApprovedAt,
                IsAdmin = d.IsAdmin,
            })
            .ToList();

    // The server keeps no denied list: it never prompts anyone to Allow, because
    // there is nobody in front of it to answer - a device pairs by redeeming a
    // code or not at all (see PairingEndpoints). So there is nothing to show.
    public Task<IReadOnlyList<DeniedPeerRow>> LoadDeniedDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeniedPeerRow>>([]);

    public Task ForgetDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) =>
        client.ForgetDeviceAsync(device.Fingerprint, ct);

    public Task ForgetDenialAsync(DeniedPeerRow device, CancellationToken ct = default) => Task.CompletedTask;

    // Nicknames are a local-display concept (DeviceNicknameStore), stored on the
    // device doing the looking. A server records the alias a peer reported for
    // itself at pairing time and has no override to write.
    public Task RenameDeviceAsync(TrustedPeerRow device, CancellationToken ct = default) =>
        throw new NotSupportedException("A server shows the name each device reports for itself.");

    public async Task<string> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) =>
        (await client.IssuePairingCodeAsync(grantsAdmin, ct)).Code;

    public async Task<IReadOnlyList<SubsonicCredentialRow>> LoadSubsonicCredentialsAsync(CancellationToken ct = default) =>
        (await client.GetSubsonicCredentialsAsync(ct)).Select(ToRow).ToList();

    public async Task<SubsonicCredentialRow> IssueSubsonicCredentialAsync(string label, CancellationToken ct = default) =>
        ToRow(await client.IssueSubsonicCredentialAsync(label, ct));

    public Task RevokeSubsonicCredentialAsync(SubsonicCredentialRow credential, CancellationToken ct = default) =>
        client.RevokeSubsonicCredentialAsync(credential.Username, ct);

    public Task RescanAsync(CancellationToken ct = default) => client.RescanAsync(ct);

    public Task RebuildDatabaseAsync(CancellationToken ct = default) =>
        throw new NotSupportedException("A server migrates its own schema on startup.");

    // A device on the roster that has never pushed answers 404, which is an
    // ordinary state rather than a failure - it has not synced since the server
    // last started, or has log sharing switched off - so it comes back as an
    // empty list for the caller to phrase.
    public async Task<IReadOnlyList<string>> LoadDeviceLogAsync(string fingerprint, int limit, CancellationToken ct = default)
    {
        try
        {
            return Render((await client.GetDeviceLogAsync(fingerprint, limit, ct)).Entries);
        }
        catch (ServerAdminException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> LoadLogAsync(int limit, CancellationToken ct = default) =>
        Render(await client.GetLogAsync(limit, ct));

    private static List<string> Render(List<AdminLogEntryDto> entries) =>
        entries
            // Rendered through the same shape the app's own Log window uses, so a
            // server's log reads identically to a local one.
            .Select(e => new Logging.InMemoryLogEntry(e.Timestamp, e.Level, e.SourceContext, e.Message, e.Exception)
                .ToDisplayLine())
            .ToList();

    // Exposed so the page can poll a rescan it started without the panel needing
    // to know there is an HTTP client behind any of this.
    public Task<AdminLibraryStatusDto> GetLibraryStatusAsync(CancellationToken ct = default) =>
        client.GetLibraryStatusAsync(ct);

    public string? DataDirectory => _lastLoaded?.DataDirectory;

    private static SubsonicCredentialRow ToRow(SubsonicCredentialDto dto) => new()
    {
        Username = dto.Username,
        Label = dto.Label,
        CreatedAt = dto.CreatedAt,
        LastSeenAt = dto.LastSeenAt,
        Password = dto.Password,
    };
}
