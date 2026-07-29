using System.Collections.Generic;
using System.Text.Json.Serialization;

using Flower.Models;
using Flower.Services;

namespace Flower.Persistence
{
    // Trim/AOT-safe source-generated metadata for every type this project
    // controls both ends of - local files (settings.json, library.json, etc.)
    // and Flower's own bespoke device-to-device wire protocol
    // (/api/flower/v1/*, see SyncHttpServer/PlaylistSyncService/
    // LibrarySyncService) - as opposed to a real third-party protocol we have
    // to match byte-for-byte (OpenSubsonic, LocalSend's /api/localsend/v2/info -
    // see ExternalProtocolJsonContext for those). WriteIndented is cosmetic
    // either way; PropertyNameCaseInsensitive costs nothing and is a bit more
    // forgiving reading a file/payload written by a slightly different
    // version of this same code.
    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(AppSettings))]
    [JsonSerializable(typeof(List<Track>), TypeInfoPropertyName = "TrackList")]
    [JsonSerializable(typeof(IEnumerable<Track>), TypeInfoPropertyName = "TrackEnumerable")]
    [JsonSerializable(typeof(List<PlaylistStore.PlaylistRecord>), TypeInfoPropertyName = "PlaylistRecordList")]
    [JsonSerializable(typeof(DeviceIdentity))]
    [JsonSerializable(typeof(PlaylistSyncStateStore.SyncStateRecord))]
    [JsonSerializable(typeof(List<DeviceNickname>), TypeInfoPropertyName = "DeviceNicknameList")]
    [JsonSerializable(typeof(PlaylistSyncManifestDto))]
    [JsonSerializable(typeof(LibrarySyncManifestDto))]
    [JsonSerializable(typeof(LogReportDto))]
    internal partial class FlowerJsonContext : JsonSerializerContext
    {
    }
}
