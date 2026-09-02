using System.Threading.Tasks;

using LibVLCSharp.Shared;

namespace Flower.Audio;

// Teaches a LibVLC core not to stall on a certificate it does not recognise.
//
// This exists because of a seam. Everything Flower fetches over HTTP goes
// through PeerHttpClient, which accepts a self-hosted server's self-signed
// certificate only when its key is one this device paired with - a real pin.
// Audio does not: TrackDecoder hands the stream URL to LibVLC
// (`new Media(libVLC, path, FromType.FromLocation)`) and LibVLC opens it with
// its own TLS stack, which knows nothing about trusted-peers.json and cannot be
// given a callback that does. Left alone it raises a dialog nobody is there to
// answer, and the track simply never starts.
//
// So the honest description of what this buys, stated plainly because it is
// weaker than the rest of the system: **audio over a self-signed certificate is
// encrypted but not authenticated.** Every other request to that server is both.
//
// Why that is an acceptable place to stand, for now:
//
//   - The origin was authenticated before this URL existed. A stream URL is
//     built for a peer that answered a signed /info over the pinned client
//     (PeerStreamUrlResolver -> PeerTrackResolver), so an attacker has to get
//     in the way of the audio connection specifically, having already failed
//     to get in the way of the one that chose it.
//   - The URL carries no reusable secret. It is signed per request with a
//     timestamp and a nonce (OpenSubsonicClient.BuildUrlAsync), not a password
//     - so capturing one yields that track, not the library. This is exactly
//     the distinction docs/OPEN-INTERNET-REVIEW.md's finding #6 draws, and the
//     credential it warns about belongs to third-party Subsonic clients, which
//     never see this certificate.
//   - It costs one track to an attacker already sitting on the wire.
//
// The way to close it is to stop letting LibVLC do the fetching: read the
// stream with the pinned HttpClient and hand LibVLC a StreamMediaInput over it,
// the way LibVlcRawStreamSink already does. That means owning seeking (range
// requests) and duration, which LibVLC currently provides for free, and it is
// deliberately not done here. See docs/REMOTE-TRANSPORT-PLAN.md.
//
// Note what this does *not* weaken: a real certificate is still validated
// normally by LibVLC, and nothing here affects any other request.
internal static class VlcCertificateDialogs
{
    // Answers every dialog rather than only the certificate one, because the
    // alternative is worse than it looks: with no handlers registered at all,
    // LibVLC falls back to its own built-in behaviour, and a headless process
    // has nothing to display it with. Dismissing a login prompt or a progress
    // dialog is the right answer here anyway - there is no user attached to a
    // decode running behind the playback pipeline.
    public static void AnswerUnattended(LibVLC libVLC)
    {
        libVLC.SetDialogHandlers(
            (_, _) => Task.CompletedTask,
            (dialog, _, _, _, _, _) =>
            {
                // No credentials to offer: Flower authenticates in the URL and
                // in headers, never through an HTTP auth prompt.
                dialog.Dismiss();
                return Task.CompletedTask;
            },
            (dialog, _, _, _, _, firstActionText, _, _) =>
            {
                // The certificate warning is a question with an accept action
                // first ("View certificate" / "Accept permanently" depending on
                // build) and it is the only question this process can meet, so
                // taking the first action is taking it. A question with no
                // actions at all can only be dismissed.
                if (string.IsNullOrEmpty(firstActionText))
                    dialog.Dismiss();
                else
                    dialog.PostAction(1);

                return Task.CompletedTask;
            },
            (dialog, _, _, _, _, _, _) =>
            {
                dialog.Dismiss();
                return Task.CompletedTask;
            },
            (_, _, _) => Task.CompletedTask);
    }
}
