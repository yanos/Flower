using Microsoft.AspNetCore.Http;

using Flower.Server.Configuration;
using Flower.Services;

namespace Flower.Server.Services;

// v1 auth: a single configured admin username/password, validated against
// the classic Subsonic token=md5(password+salt) scheme every real Subsonic
// client (including Flower.Core's own OpenSubsonicClient) already sends -
// see SYNC-PLAN.md's "Pairing redesign" section for the later, separate
// admin-session auth the browser UI will need.
public static class SubsonicAuth
{
    public static bool Validate(IQueryCollection query, FlowerServerOptions options)
    {
        var username = query["u"].ToString();
        var token = query["t"].ToString();
        var salt = query["s"].ToString();

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(salt))
            return false;

        if (!string.Equals(username, options.AdminUsername, StringComparison.Ordinal))
            return false;

        var expected = OpenSubsonicClient.ComputeToken(options.AdminPassword, salt);
        return string.Equals(token, expected, StringComparison.OrdinalIgnoreCase);
    }
}
