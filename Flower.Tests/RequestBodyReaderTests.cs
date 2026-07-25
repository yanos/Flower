using System.IO;
using System.Text;
using System.Threading.Tasks;

using Flower.Services;

namespace Flower.Tests;

public class RequestBodyReaderTests
{
    [Fact]
    public async Task ReadWithCapAsync_returns_the_full_body_when_under_the_cap()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        using var stream = new MemoryStream(bytes);

        var result = await RequestBodyReader.ReadWithCapAsync(stream, contentLengthHeader: bytes.Length, maxBytes: 1024);

        Assert.Equal(bytes, result);
    }

    [Fact]
    public async Task ReadWithCapAsync_rejects_via_content_length_header_without_reading()
    {
        using var stream = new MemoryStream(new byte[10]);

        var result = await RequestBodyReader.ReadWithCapAsync(stream, contentLengthHeader: 1_000_000, maxBytes: 1024);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadWithCapAsync_rejects_when_the_actual_stream_exceeds_the_cap_despite_a_lying_content_length()
    {
        var bytes = new byte[2048];
        using var stream = new MemoryStream(bytes);

        // Content-Length under-reports the real size - defense-in-depth must
        // still catch this by actually bounding the read, not trusting the header alone.
        var result = await RequestBodyReader.ReadWithCapAsync(stream, contentLengthHeader: 10, maxBytes: 1024);

        Assert.Null(result);
    }
}
