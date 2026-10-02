using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Flower.Services;

// How Flower.Server says no, on every route - docs/TRUST-BOUNDARY-PLAN.md step
// 3. An RFC 9457 problem document (application/problem+json) with one member
// of Flower's own, Code, which is the part a client acts on.
//
// The status says what to do next and the code says why. Before this the
// status was made to carry both: 403 meant "revoked" on the device routes and
// "not an admin" on the admin routes, 401 meant both "unknown device" and
// "stale signature" on the admin routes, and a client could only guess which
// it had been told. AWS's request signing has the same shape - every
// authentication failure is one status, and the body names which
// (SignatureDoesNotMatch, RequestTimeTooSkewed). Here the statuses mean the
// same on every surface: 401 is "authentication failed", 403 is
// "authenticated, and not allowed".
public sealed record FlowerProblemDto(
    string Type,
    string Title,
    int Status,
    string Code,
    string? Detail = null,
    // Only on clock-skew: the server's own time, so a client whose clock is
    // wrong can sign with the server's instead (see SignatureClock).
    DateTimeOffset? ServerTime = null);

public static class ProblemCodes
{
    // 400 - the request itself is malformed; sending it again will not help.
    public const string InvalidRequest = "invalid-request";
    // 400 - pair-redeem: the code is wrong, expired, or already used.
    public const string PairingCodeInvalid = "pairing-code-invalid";

    // 401 - no key on file for the fingerprint claimed, or none claimed. The
    // one refusal that can mean a pairing is over, which is why step 4 makes
    // the server sign it before a client acts on it.
    public const string DeviceUnknown = "device-unknown";
    // 401 - a key is on file and this signature does not verify against it.
    public const string SignatureInvalid = "signature-invalid";
    // 401 - the timestamp is outside the window; carries ServerTime.
    public const string ClockSkew = "clock-skew";
    // 401 - this nonce has been seen. A client that signs every attempt
    // afresh never gets this; one that does not has a bug (see
    // PeerCredentialsHandler, and CITED-DECISIONS.md #2c).
    public const string NonceReused = "nonce-reused";

    // 403 - paired and verified, and this route is an administrator's.
    public const string NotAdmin = "not-admin";

    public const string NotFound = "not-found";
    public const string Conflict = "conflict";
    public const string LengthRequired = "length-required";
    public const string TooLarge = "too-large";
    public const string Corrupt = "corrupt";
    public const string RateLimited = "rate-limited";
    // 503 - a library scan is running; upload, move and edit routes wait.
    public const string Scanning = "scanning";
    // 503 - nowhere to write, or something else about the server.
    public const string Unavailable = "unavailable";
    public const string ServerError = "server-error";

    // The code a status gets when nothing more specific was said - what the
    // server's fallback writes for a refusal that left without a body. Every
    // 401 and 403 the server sends names its code explicitly; should one
    // ever leave without, it is given the reading that costs a client
    // nothing - try again, or hide a control - and never device-unknown,
    // because guessing that one is how a client unpairs over a stale
    // timestamp.
    public static string ForStatus(int status) => status switch
    {
        400 => InvalidRequest,
        401 => SignatureInvalid,
        403 => NotAdmin,
        404 => NotFound,
        409 => Conflict,
        411 => LengthRequired,
        413 => TooLarge,
        422 => Corrupt,
        429 => RateLimited,
        503 => Unavailable,
        _ => ServerError,
    };
}

public static class FlowerProblem
{
    public const string ContentType = "application/problem+json";

    public static FlowerProblemDto Create(int status, string code, string? detail = null, DateTimeOffset? serverTime = null) =>
        new($"urn:flower:problem:{code}", TitleFor(code), status, code, detail, serverTime);

    public static string Serialize(FlowerProblemDto problem) =>
        JsonSerializer.Serialize(problem, FlowerProblemJsonContext.Default.FlowerProblemDto);

    // Null for anything that is not one: a proxy's HTML error page, an empty
    // body, a body from something that is not Flower.Server.
    public static FlowerProblemDto? TryRead(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || !body.TrimStart().StartsWith('{'))
            return null;

        try
        {
            var problem = JsonSerializer.Deserialize(body, FlowerProblemJsonContext.Default.FlowerProblemDto);
            return problem is { Code.Length: > 0 } ? problem : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The sentence a person sees when the route that refused did not say
    // anything more specific - in Detail, which is preferred whenever present.
    private static string TitleFor(string code) => code switch
    {
        ProblemCodes.InvalidRequest => "The request was not understood.",
        ProblemCodes.PairingCodeInvalid => "Invalid, expired, or already-used pairing code.",
        ProblemCodes.DeviceUnknown => "This server has no key for that device.",
        ProblemCodes.SignatureInvalid => "The request's signature did not verify.",
        ProblemCodes.ClockSkew => "The request's clock is too far from the server's.",
        ProblemCodes.NonceReused => "That request has already been made.",
        ProblemCodes.NotAdmin => "This device is paired, but is not an administrator of this server.",
        ProblemCodes.NotFound => "Nothing here by that name.",
        ProblemCodes.Conflict => "Something else is already there.",
        ProblemCodes.LengthRequired => "A request body must state its length.",
        ProblemCodes.TooLarge => "The request is larger than this route accepts.",
        ProblemCodes.Corrupt => "What arrived does not match what was promised.",
        ProblemCodes.RateLimited => "Too many requests. Wait and try again.",
        ProblemCodes.Scanning => "This server is scanning its library; try again when it has finished.",
        ProblemCodes.Unavailable => "This server cannot do that right now.",
        _ => "The server could not answer that.",
    };
}

// RFC 9457 spells its members in lower case, so the wire is camelCase here
// even on the routes whose own bodies are PascalCase.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FlowerProblemDto))]
public partial class FlowerProblemJsonContext : JsonSerializerContext
{
}

// A refusal from a Flower server, read: the status and, when the body was a
// problem document, what it said. An HttpRequestException so everything that
// already catches one - every sync path's "could not reach" handling - still
// does, with the code now there for the callers that act on why.
public sealed class PeerRefusedException(HttpStatusCode status, FlowerProblemDto? problem, string message)
    : HttpRequestException(message, null, status)
{
    public FlowerProblemDto? Problem { get; } = problem;

    public string? Code => Problem?.Code;
}

public static class PeerResponses
{
    // EnsureSuccessStatusCode, keeping the reason. A response that is not a
    // problem document still throws, with Problem null - a proxy's error page
    // is a refusal too, just one that says nothing a client can act on.
    public static async Task EnsureSuccessAsync(this HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
            return;

        FlowerProblemDto? problem = null;
        try
        {
            if (response.Content.Headers.ContentType?.MediaType == FlowerProblem.ContentType)
                problem = FlowerProblem.TryRead(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (HttpRequestException)
        {
            // The status is the finding; the body would only have said why.
        }

        var described = problem == null ? "" : $" ({problem.Code}{(problem.Detail is { Length: > 0 } detail ? ": " + detail : "")})";
        throw new PeerRefusedException(
            response.StatusCode, problem,
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}){described}.");
    }

    // The refusal that can mean a pairing is over: no key on file for this
    // device. Step 3 names it; step 4 makes the server sign it.
    public static bool IsDeviceUnknown(Exception ex) =>
        ex is PeerRefusedException { Code: ProblemCodes.DeviceUnknown };
}
