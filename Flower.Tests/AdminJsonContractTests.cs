using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Flower.Persistence;
using Flower.Services;

using Xunit;

namespace Flower.Tests;

// What keeps the admin API usable from Flower.Web.
//
// That head is trimmed, and a trimmed build has reflection-based JSON disabled
// outright: JsonSerializer throws NotSupportedException the moment it is asked
// for a type it has no source-generated metadata for. On the desktop, where
// reflection is available, the exact same code just works - so nothing in this
// suite would notice a DTO that was never added to FlowerJsonContext. The
// symptom in the browser is not an obvious crash either: the settings page draws
// its tabs and buttons and then fills in nothing at all.
public class AdminJsonContractTests
{
    // Every type ServerAdminClient actually puts on the wire, read off its own
    // signatures rather than listed by hand - a new endpoint added there is
    // covered here the moment it is written, which is the whole point.
    public static IEnumerable<object[]> WireTypes() =>
        typeof(ServerAdminClient)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters()
                .Select(parameter => parameter.ParameterType)
                .Append(Payload(method.ReturnType)))
            .Where(IsSerialized)
            .Distinct()
            .Select(type => new object[] { type });

    // Task<T> carries the response body; a bare Task means the call has none.
    private static Type? Payload(Type returnType) =>
        returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>)
            ? returnType.GetGenericArguments()[0]
            : null;

    // The DTOs, and only those: the rest of what these signatures mention is
    // cancellation tokens and the odd bool/string/int that travels in the query
    // string, none of which is ever serialized.
    private static bool IsSerialized(Type? type) =>
        type != null && type != typeof(CancellationToken) && !type.IsPrimitive && type != typeof(string);

    [Theory]
    [MemberData(nameof(WireTypes))]
    public void Every_admin_dto_has_source_generated_metadata(Type type)
    {
        Assert.NotNull(FlowerJsonContext.Default.GetTypeInfo(type));
    }

    // The metadata above is only reached if the client's options actually ask
    // for it. Left to its defaults, JsonSerializerOptions resolves by reflection
    // and every one of these calls throws on a trimmed head no matter how
    // complete the context is.
    [Fact]
    public void The_admin_client_serializes_through_that_context_rather_than_reflection()
    {
        var options = (JsonSerializerOptions)typeof(ServerAdminClient)
            .GetField("Json", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        Assert.Same(FlowerJsonContext.Default, options.TypeInfoResolver);
    }

    // The server answers camelCase (ASP.NET Core's web defaults) and reads the
    // update body case-insensitively, so the context's own naming has to give
    // way to the client's web defaults rather than the other way round -
    // source-generated metadata is easy to wire up in a way that quietly writes
    // PascalCase instead.
    [Fact]
    public void The_wire_shape_is_the_camelCase_one_the_server_speaks()
    {
        var options = (JsonSerializerOptions)typeof(ServerAdminClient)
            .GetField("Json", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        var settings = JsonSerializer.Deserialize<ServerSettingsDto>(
            """
            {"alias":"Basement","advertisedHost":"nas.local","advertiseOnLan":true,
             "trustTailscaleRange":false,"allowedCidrs":["10.0.0.0/8"],
             "libraryPaths":["/music"],"dataDirectory":"/data","version":"1.2.3"}
            """,
            options);

        Assert.Equal("Basement", settings!.Alias);
        Assert.Equal("nas.local", settings.AdvertisedHost);
        Assert.False(settings.TrustTailscaleRange);
        Assert.Equal(["10.0.0.0/8"], settings.AllowedCidrs);

        var update = JsonSerializer.Serialize(
            new ServerSettingsUpdateDto("Basement", null, null, null, null, null), options);

        Assert.Contains("\"alias\":\"Basement\"", update);
        // Null means "leave this one alone" (AdminEndpoints tests each field
        // with `is { }`), which an explicit null on the wire says just as well
        // as an omitted property - what matters is that it comes back null
        // rather than as an empty string that would clear the value.
        Assert.Null(JsonSerializer.Deserialize<ServerSettingsUpdateDto>(update, options)!.AdvertisedHost);
    }
}
