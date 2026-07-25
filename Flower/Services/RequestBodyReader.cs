using System;
using System.IO;
using System.Threading.Tasks;

namespace Flower.Services;

// Reads an HTTP request body into memory with a hard cap - extracted as a
// plain Stream-based method (rather than living inline against
// HttpListenerContext) specifically so it's unit-testable against a
// MemoryStream without spinning up a real listener. Used by SyncHttpServer's
// dispatcher as the single place every gated body-carrying request gets read,
// so the same bytes can be hashed for signature verification (see
// SignedRequestCanonicalizer) without a handler needing to read the stream
// itself.
public static class RequestBodyReader
{
    public static async Task<byte[]?> ReadWithCapAsync(Stream input, long contentLengthHeader, long maxBytes)
    {
        // Fast path when the client sent an honest Content-Length - avoids
        // reading a single byte of an obviously-oversized body.
        if (contentLengthHeader > maxBytes)
            return null;

        using var buffered = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        // Defense-in-depth against a missing/lied-about Content-Length or
        // chunked encoding - don't trust the header alone to bound memory use.
        while ((read = await input.ReadAsync(chunk)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return null;
            buffered.Write(chunk, 0, read);
        }
        return buffered.ToArray();
    }
}
