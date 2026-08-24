using System.Collections.Generic;
using System.Text.Json.Serialization;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence
{
    // Trim/AOT-safe source-generated metadata for every type this project
    // controls both ends of - local files (settings.json, library.json, etc.)
    // and Flower's own bespoke device-to-device wire protocol
    // (/api/flower/v1/*, see PlaylistSyncService/LibrarySyncService) - as opposed to a real third-party protocol we have
    // to match byte-for-byte (OpenSubsonic, LocalSend's /api/localsend/v2/info -
    // see ExternalProtocolJsonContext for those). WriteIndented is cosmetic
    // either way; PropertyNameCaseInsensitive costs nothing and is a bit more
    // forgiving reading a file/payload written by a slightly different
    // version of this same code.
    //
    // WriteIndented is off and nulls are omitted purely for size: library.json
    // at the 16k-track scale this app targets was ~17.9 MB, roughly 60% of it
    // indentation and null-valued properties spelled out in full, and it is
    // rewritten whenever track stats change. Both are safe here precisely
    // because this context covers only formats Flower controls both ends of -
    // a reader of a missing property gets the same default it would have got
    // reading an explicit null. See docs/ARCHITECTURE-REVIEW.md Tier 1.1.
    [JsonSourceGenerationOptions(
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(List<Track>), TypeInfoPropertyName = "TrackList")]
    [JsonSerializable(typeof(IEnumerable<Track>), TypeInfoPropertyName = "TrackEnumerable")]
    [JsonSerializable(typeof(List<JsonLibraryImport.PlaylistRecord>), TypeInfoPropertyName = "PlaylistRecordList")]
    [JsonSerializable(typeof(DeviceIdentity))]
    [JsonSerializable(typeof(PlaylistSyncStateStore.SyncStateRecord))]
    [JsonSerializable(typeof(List<DeviceNickname>), TypeInfoPropertyName = "DeviceNicknameList")]
    [JsonSerializable(typeof(PlaylistSyncManifestDto))]
    [JsonSerializable(typeof(LibrarySyncManifestDto))]
    [JsonSerializable(typeof(LogReportDto))]
    [JsonSerializable(typeof(StreamTicketDto))]

    // The server's /api/admin surface (see ServerAdminClient). Registered here
    // for the same reason as the sync DTOs above - Flower writes both ends of
    // it - but load-bearing rather than cosmetic on one head in particular:
    // Flower.Web is trimmed, so reflection-based serialization is disabled
    // outright there and every one of these calls threw
    // NotSupportedException("JsonSerializer.IsReflectionEnabledByDefault") -
    // which surfaced as a settings page that drew its chrome and then filled in
    // nothing.
    [JsonSerializable(typeof(ServerSettingsDto))]
    [JsonSerializable(typeof(ServerSettingsUpdateDto))]
    [JsonSerializable(typeof(List<AdminDeviceDto>), TypeInfoPropertyName = "AdminDeviceList")]
    [JsonSerializable(typeof(AdminPairingCodeDto))]
    [JsonSerializable(typeof(AdminLibraryStatusDto))]
    [JsonSerializable(typeof(List<AdminLogEntryDto>), TypeInfoPropertyName = "AdminLogEntryList")]
    [JsonSerializable(typeof(AdminDeviceLogDto))]
    [JsonSerializable(typeof(SubsonicCredentialDto))]
    [JsonSerializable(typeof(List<SubsonicCredentialDto>), TypeInfoPropertyName = "SubsonicCredentialList")]
    internal partial class FlowerJsonContext : JsonSerializerContext
    {
    }
}
