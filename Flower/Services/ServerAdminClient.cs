using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Flower.Persistence;

namespace Flower.Services;

// Wire shapes for Flower.Server's /api/admin surface (see
// Flower.Server/Endpoints/AdminEndpoints.cs). Written here rather than shared
// from Flower.Server: the server project references this one, not the other way
// round, and the browser head has no business taking a dependency on Kestrel to
// name a DTO. The server therefore declares its own matching records - except
// for the settings pair, which is the one shape both sides had started *editing*
// in step with each other, and which now lives in Flower.Core beside the other
// contracts both hosts share (Services/ServerAdminContracts.cs).
public sealed record AdminDeviceDto(string Fingerprint, string Alias, DateTimeOffset ApprovedAt, bool IsAdmin);
public sealed record AdminPairingCodeDto(string Code, DateTimeOffset ExpiresAt, bool GrantsAdmin, string Invite, string BrowserUrl);
public sealed record AdminLibraryStatusDto(bool Rescanning, int TrackCount, DateTimeOffset? LastCompletedAt, string? LastError);
public sealed record AdminLogEntryDto(DateTimeOffset Timestamp, string Level, string? SourceContext, string Message, string? Exception);
public sealed record SubsonicCredentialDto(
    string Username, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, string? Password);

// Raised for any non-success response, so callers can surface the server's own
// message instead of a bare status code - "A device cannot revoke itself." is
// worth showing verbatim.
public sealed class ServerAdminException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    // The two the UI treats specially: not paired at all, versus paired but not
    // an admin. Both are ordinary outcomes of clicking the button, not faults.
    public bool IsAuthFailure => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
}

// Typed client for a Flower server's admin API.
//
// Authentication is left to the caller through authorize, but there is only one
// way to authenticate now: an IPeerCredentials signature. The browser used to be
// the exception, presenting a server-minted session token because
// .NET-for-WebAssembly cannot sign - it now signs through WebCrypto like every
// other head (see BrowserPeerCredentials), which is why ForSession and the
// bearer path it fed are gone. The delegate stays because tests still supply
// their own, and because the admin surface has no business knowing how a caller
// proved itself.
public sealed class ServerAdminClient(HttpClient http, Uri baseAddress, Func<HttpRequestMessage, byte[], Task> authorize)
{
    // Web defaults for the naming policy (the server answers camelCase), but with
    // the source-generated resolver supplying the metadata: Flower.Web is trimmed,
    // and a trimmed build has reflection-based serialization disabled entirely, so
    // resolving these types by reflection throws there rather than merely being
    // slower. See FlowerJsonContext.
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { TypeInfoResolver = FlowerJsonContext.Default };

    public Uri BaseAddress { get; } = baseAddress;

    public Task<ServerSettingsDto> GetSettingsAsync(CancellationToken ct = default) =>
        SendAsync<ServerSettingsDto>(HttpMethod.Get, "/api/admin/settings", null, ct);

    public Task<ServerSettingsDto> UpdateSettingsAsync(ServerSettingsUpdateDto update, CancellationToken ct = default) =>
        SendAsync<ServerSettingsDto>(HttpMethod.Put, "/api/admin/settings", update, ct);

    public Task<List<AdminDeviceDto>> GetDevicesAsync(CancellationToken ct = default) =>
        SendAsync<List<AdminDeviceDto>>(HttpMethod.Get, "/api/admin/devices", null, ct);

    public Task ForgetDeviceAsync(string fingerprint, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/api/admin/devices/{Uri.EscapeDataString(fingerprint)}", null, ct);

    public Task<AdminPairingCodeDto> IssuePairingCodeAsync(bool grantsAdmin, CancellationToken ct = default) =>
        SendAsync<AdminPairingCodeDto>(
            HttpMethod.Post, $"/api/admin/pairing-codes?grantsAdmin={(grantsAdmin ? "true" : "false")}", null, ct);

    public Task<AdminLibraryStatusDto> GetLibraryStatusAsync(CancellationToken ct = default) =>
        SendAsync<AdminLibraryStatusDto>(HttpMethod.Get, "/api/admin/library", null, ct);

    public Task<AdminLibraryStatusDto> RescanAsync(CancellationToken ct = default) =>
        SendAsync<AdminLibraryStatusDto>(HttpMethod.Post, "/api/admin/library/rescan", null, ct);

    public Task<List<AdminLogEntryDto>> GetLogAsync(int limit, CancellationToken ct = default) =>
        SendAsync<List<AdminLogEntryDto>>(HttpMethod.Get, $"/api/admin/logs?limit={limit}", null, ct);

    public Task<List<SubsonicCredentialDto>> GetSubsonicCredentialsAsync(CancellationToken ct = default) =>
        SendAsync<List<SubsonicCredentialDto>>(HttpMethod.Get, "/api/admin/subsonic-credentials", null, ct);

    public Task<SubsonicCredentialDto> IssueSubsonicCredentialAsync(string label, CancellationToken ct = default) =>
        SendAsync<SubsonicCredentialDto>(
            HttpMethod.Post, $"/api/admin/subsonic-credentials?label={Uri.EscapeDataString(label)}", null, ct);

    public Task RevokeSubsonicCredentialAsync(string username, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/api/admin/subsonic-credentials/{Uri.EscapeDataString(username)}", null, ct);

    private async Task<T> SendAsync<T>(HttpMethod method, string pathAndQuery, object? body, CancellationToken ct)
    {
        var response = await SendAsync(method, pathAndQuery, body, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new ServerAdminException(response.StatusCode, "The server returned an empty response.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string pathAndQuery, object? body, CancellationToken ct)
    {
        // Serialized up front rather than left to HttpContent: the signature
        // covers a hash of the exact bytes sent, so the authorizer has to see
        // them before the request goes out.
        var payload = body == null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, Json);

        var request = new HttpRequestMessage(method, new Uri(BaseAddress, pathAndQuery));
        if (body != null)
            request.Content = new ByteArrayContent(payload) { Headers = { ContentType = new("application/json") } };

        await authorize(request, payload);

        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ServerAdminException(response.StatusCode, await DescribeFailureAsync(response, ct));

        return response;
    }

    // The admin API answers a refusal with {"error": "..."} - worth showing, and
    // worth falling back gracefully when the body is something else entirely (a
    // proxy's HTML error page, an empty 403).
    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(text) && text.TrimStart().StartsWith('{'))
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } message)
                    return message;
            }
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or OperationCanceledException)
        {
            // Fall through to the status-only description below.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "This device is not paired with that server.",
            HttpStatusCode.Forbidden => "This device is paired, but is not an administrator of that server.",
            _ => $"The server refused the request ({(int)response.StatusCode} {response.ReasonPhrase}).",
        };
    }

    // The identity block, the canonical form and the query parsing all live in
    // IPeerCredentials now - this used to be a fifth hand-rolled copy of them.
    public static Func<HttpRequestMessage, byte[], Task> SignWith(IPeerCredentials credentials) =>
        (request, body) => request.AddPeerCredentialsAsync(credentials, body);
}
