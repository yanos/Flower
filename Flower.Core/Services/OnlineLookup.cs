using System;
using System.Net.Http;

namespace Flower.Services;

// What every keyless public service Flower asks something of is told about
// who is asking. MusicBrainz refuses a request that does not say, and LRCLIB
// asks for a descriptive one, so it is said the same way to all of them:
// Track Info's cover search (OnlineCoverArt) and the online lookups
// (OnlineMetadataEngine) alike.
public static class OnlineLookup
{
    public static readonly string UserAgent = $"Flower/{AppVersion.Display} ( https://github.com/yanos/Flower )";

    // A client for plain public GETs, with nothing of PeerHttpClient's
    // signing or certificate rule: these go to the internet, not to a peer.
    public static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.TryParseAdd(UserAgent);
        return http;
    }
}
