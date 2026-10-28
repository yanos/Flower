using System;
using System.Globalization;

namespace Flower.Services
{
    // Semantic-version precedence (semver.org, section 11), for telling
    // whether a release is newer than the build that is running. Velopack
    // does its own on Windows and Linux; this is for GitHubReleaseUpdater,
    // which compares a tag with AppVersion itself.
    public static class ReleaseVersion
    {
        // Whether `candidate` comes after `current`. A leading "v" (a tag)
        // and any "+build" metadata (MinVer's commit) are ignored. Either one
        // not being a version at all is never newer: a release this cannot
        // read is not one to offer.
        public static bool IsNewer(string candidate, string current) =>
            Compare(candidate, current) is > 0;

        // Null when either side is not a version.
        public static int? Compare(string a, string b)
        {
            if (Parse(a) is not { } left || Parse(b) is not { } right)
                return null;

            for (var i = 0; i < 3; i++)
            {
                var core = left.Core[i].CompareTo(right.Core[i]);
                if (core != 0)
                    return core;
            }

            // 1.0.0-beta < 1.0.0: a pre-release comes before its release.
            if (left.PreRelease is null || right.PreRelease is null)
                return (left.PreRelease is null).CompareTo(right.PreRelease is null);

            var leftIds = left.PreRelease.Split('.');
            var rightIds = right.PreRelease.Split('.');
            for (var i = 0; i < Math.Min(leftIds.Length, rightIds.Length); i++)
            {
                var compared = CompareIdentifier(leftIds[i], rightIds[i]);
                if (compared != 0)
                    return compared;
            }

            return leftIds.Length.CompareTo(rightIds.Length);
        }

        // Numbers compare as numbers and come before words.
        private static int CompareIdentifier(string a, string b)
        {
            var aIsNumber = ulong.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var aNumber);
            var bIsNumber = ulong.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bNumber);
            if (aIsNumber && bIsNumber)
                return aNumber.CompareTo(bNumber);
            if (aIsNumber != bIsNumber)
                return aIsNumber ? -1 : 1;
            return string.CompareOrdinal(a, b);
        }

        private static (int[] Core, string? PreRelease)? Parse(string version)
        {
            var text = AppVersion.StripBuildMetadata(version.Trim());
            if (text.StartsWith('v') || text.StartsWith('V'))
                text = text[1..];

            string? preRelease = null;
            var dash = text.IndexOf('-');
            if (dash >= 0)
            {
                preRelease = text[(dash + 1)..];
                text = text[..dash];
                if (preRelease.Length == 0)
                    return null;
            }

            var parts = text.Split('.');
            if (parts.Length is < 1 or > 3)
                return null;

            var core = new int[3];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out core[i]))
                    return null;
            }

            return (core, preRelease);
        }
    }
}
