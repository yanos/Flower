namespace Flower.Services;

// Whose copy of something a request reads and writes, on a server shared by
// an owner and a few listeners. Flower has devices rather than accounts, so a
// listener is drawn the way the rest of the protocol draws the line: the
// owner's devices - the admins - are one listener and share everything, and
// every other device is a listener of its own.
//
// The Continue Playing shelf was the first thing kept this way (see
// AlbumProgressLedger) and playlists are the second: a housemate's phone can
// keep playlists on the server without being able to read, rename or delete
// the owner's. See docs/TRUST-BOUNDARY-PLAN.md, step 1.
//
// Decided from the fingerprint a request signature proved and the trust
// store's admin flag, never from anything a request body says.
public static class Listeners
{
    // The owner's listener - every admin device. Also the value every playlist
    // a client keeps in its own database carries, since a client's playlists
    // are its user's own.
    public const string Owner = "owner";

    public static string For(string fingerprint, bool isAdmin) => isAdmin ? Owner : fingerprint;
}
