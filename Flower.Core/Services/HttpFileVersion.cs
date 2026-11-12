using System.Net.Http;
using System.Net.Http.Headers;

namespace Flower.Services;

// Which version of a file a media response is of, in the form If-Range takes
// back: a strong ETag, which is what Flower.Server sends
// (MediaEndpoints.VersionOf), or failing that its date. A weak ETag is no use,
// because If-Range refuses to compare one. Whatever picks a file up partway
// through sends it, so the server answers with the rest of the same file or
// the whole of a changed one, never a range of a file it was not reading:
// SeekableHttpStream reopening a song, PeerMediaClient resuming a download.
internal static class HttpFileVersion
{
    public static RangeConditionHeaderValue? Of(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false } tag ? new RangeConditionHeaderValue(tag)
        : response.Content.Headers.LastModified is { } date ? new RangeConditionHeaderValue(date)
        : null;
}
