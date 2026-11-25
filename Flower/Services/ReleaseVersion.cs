using System;
using System.Globalization;
using System.Text.RegularExpressions;

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

        // Whether `version` reads as one, and its pre-release label if it has
        // one ("beta.2" in "v1.2.0-beta.2+abc"), null for a release.
        public static bool TryGetPreRelease(string version, out string? preRelease)
        {
            preRelease = null;
            if (Parse(version) is not { } parsed)
                return false;
            preRelease = parsed.PreRelease;
            return true;
        }

        // Numbers compare as numbers and come before words.
        private static int CompareIdentifier(string a, string b)
        {
            var aIsNumber = IsNumber(a);
            var bIsNumber = IsNumber(b);
            if (aIsNumber && bIsNumber)
            {
                a = a.TrimStart('0');
                b = b.TrimStart('0');
                return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            }
            if (aIsNumber != bIsNumber)
                return aIsNumber ? -1 : 1;
            return string.CompareOrdinal(a, b);
        }

        private static bool IsNumber(string value)
        {
            foreach (var c in value)
            {
                if (c is < '0' or > '9')
                    return false;
            }
            return value.Length > 0;
        }

        private static readonly Regex VersionPattern = new(
            @"^[vV]?([0-9]+(?:\.[0-9]+){0,2})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

        private static (int[] Core, string? PreRelease)? Parse(string version)
        {
            var match = VersionPattern.Match(version.Trim());
            if (!match.Success)
                return null;
            var parts = match.Groups[1].Value.Split('.');
            var core = new int[3];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out core[i]))
                    return null;
            }
            return (core, match.Groups[2].Success ? match.Groups[2].Value : null);
        }
    }
}
