using System.Text;

using Flower.Services;

namespace Flower.Server.Services;

// Every refusal this server sends, as the one shape a client reads - see
// FlowerProblem for the contract and docs/TRUST-BOUNDARY-PLAN.md step 3 for
// why the status alone stopped being enough.
public static class Problems
{
    public static IResult Of(int status, string code, string? detail = null, DateTimeOffset? serverTime = null) =>
        Results.Text(
            FlowerProblem.Serialize(FlowerProblem.Create(status, code, detail, serverTime)),
            FlowerProblem.ContentType, Encoding.UTF8, status);

    public static IResult BadRequest(string? detail = null) =>
        Of(StatusCodes.Status400BadRequest, ProblemCodes.InvalidRequest, detail);

    public static IResult NotFound(string? detail = null) =>
        Of(StatusCodes.Status404NotFound, ProblemCodes.NotFound, detail);

    public static IResult NotAdmin() =>
        Of(StatusCodes.Status403Forbidden, ProblemCodes.NotAdmin);

    public static IResult DeviceUnknown() =>
        Of(StatusCodes.Status401Unauthorized, ProblemCodes.DeviceUnknown);

    // Which part of a signature failed, as the code a client acts on:
    // clock-skew carries the server's time so the client can correct its
    // own, and the rest mean "sign again".
    public static IResult BadSignature(SignatureCheck problem) => problem switch
    {
        SignatureCheck.Stale => Of(StatusCodes.Status401Unauthorized, ProblemCodes.ClockSkew, serverTime: DateTimeOffset.UtcNow),
        SignatureCheck.Replayed => Of(StatusCodes.Status401Unauthorized, ProblemCodes.NonceReused),
        _ => Of(StatusCodes.Status401Unauthorized, ProblemCodes.SignatureInvalid),
    };

    // RequestGate's refusals, the same on every surface it guards.
    public static IResult ForGate(RequestGate.Result result, HttpContext http) => result.Outcome switch
    {
        RequestGate.Outcome.LengthRequired => Of(StatusCodes.Status411LengthRequired, ProblemCodes.LengthRequired),
        RequestGate.Outcome.TooLarge => Of(StatusCodes.Status413PayloadTooLarge, ProblemCodes.TooLarge),
        RequestGate.Outcome.Throttled => RateLimitResponse.TooManyRequests(http),
        RequestGate.Outcome.Unknown => DeviceUnknown(),
        RequestGate.Outcome.BadSignature => BadSignature(result.SignatureProblem),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, "Admitted is not a refusal."),
    };

    // The safety net under all of the above (Program.cs): a refusal under /api
    // that left with no body of its own - Results.StatusCode, a bare NotFound,
    // the single-page fallback's 404 - gets one by its status, so there is no
    // refusal a client has to read from the status alone.
    public static async Task FillEmptyRefusalAsync(HttpContext context)
    {
        var response = context.Response;
        if (response.HasStarted
            || response.StatusCode < 400
            || !context.Request.Path.StartsWithSegments("/api")
            || response.ContentLength > 0
            || !string.IsNullOrEmpty(response.ContentType))
        {
            return;
        }

        var problem = FlowerProblem.Create(response.StatusCode, ProblemCodes.ForStatus(response.StatusCode));
        response.ContentType = FlowerProblem.ContentType;
        await response.WriteAsync(FlowerProblem.Serialize(problem), context.RequestAborted);
    }
}
