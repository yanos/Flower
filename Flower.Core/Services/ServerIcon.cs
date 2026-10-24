using System;
using System.Globalization;
using System.Security.Cryptography;

namespace Flower.Services;

// What a server shows beside its name in a client's sidebar, in place of the
// generic server glyph: an emoji, or a picture its owner chose. One or the
// other, never both - setting either clears the other on the server.
//
// The emoji travels on /info to anyone who asks, like the name it sits beside.
// The picture does not: /info carries only a token naming its current bytes,
// and only to a caller whose signature verified, and the bytes themselves are
// on the sync surface behind the same gate as album art. So an unpaired
// server's row shows its emoji or the generic glyph, and a picture is for the
// devices the owner let in.
public static class ServerIcon
{
    // The picture, for a paired device (SyncEndpoints).
    public const string ImagePath = "/api/flower/v1/server-icon";

    // Setting or removing the picture, for an administrator (AdminEndpoints):
    // PUT with the image as the body, DELETE to take it away.
    public const string AdminImagePath = "/api/admin/server-icon";

    // A client scales what it is given down to this before sending it, and
    // the server refuses anything larger than MaxImageBytes: a sidebar draws
    // it at 20 pixels, and the largest anything draws it is a few times that.
    public const int ImagePixels = 256;
    public const int MaxImageBytes = 512 * 1024;

    // The emoji as stored and shown: the first thing a reader would call one
    // character - an emoji built of several code points (a flag, a family, a
    // skin tone) included - with the rest dropped. Empty for none.
    public static string CleanEmoji(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length == 0 ? "" : trimmed[..StringInfo.GetNextTextElementLength(trimmed)];
    }

    // Names one picture's bytes, so a client can tell from /info alone
    // whether the picture it holds is still the server's.
    public static string TokenOf(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
}
