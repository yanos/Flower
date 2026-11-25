using System;
using System.Text.Json;
using Flower.Services;

namespace Flower.Tests.AppShell;

public class ReleasePolicyConformanceTests
{
    [Fact]
    public void Desktop_policy_matches_the_shared_image_channel_vectors()
    {
        using var stream = typeof(ReleasePolicyConformanceTests).Assembly.GetManifestResourceStream("ReleasePolicyVectors.json")!;
        using var doc = JsonDocument.Parse(stream);
        foreach (var item in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            var tag = item.GetProperty("tag").GetString()!;
            Assert.Equal(item.GetProperty("valid").GetBoolean(), ReleaseVersion.TryGetPreRelease(tag, out _));
            Assert.Equal(item.GetProperty("stable").GetBoolean(), UpdateChannel.Stable.Offers(tag));
            Assert.Equal(item.GetProperty("beta").GetBoolean(), UpdateChannel.Beta.Offers(tag));
        }
        foreach (var item in doc.RootElement.GetProperty("comparisons").EnumerateArray())
        {
            var compared = ReleaseVersion.Compare(item.GetProperty("left").GetString()!, item.GetProperty("right").GetString()!);
            int? expected = item.GetProperty("result").ValueKind == JsonValueKind.Null ? null : item.GetProperty("result").GetInt32();
            Assert.Equal(expected, compared is { } n ? Math.Sign(n) : null);
        }
    }
}
