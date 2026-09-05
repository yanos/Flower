using System;

namespace Flower.Logging
{
    // Shortens a track path for a log line.
    //
    // A local track's path is a filename and is logged as-is. A remote one is
    // an OpenSubsonic stream URL carrying the whole authenticated request in
    // its query string - roughly 900 characters of it - and two things are
    // wrong with logging that whole. It swamps the line it appears on, and it
    // contains the caller's `t=` token, which is a credential that a client
    // then pushes to the server's device log where it sits at rest.
    //
    // What is actually wanted from a stream URL is which server and which
    // track, so that is what survives: host, path, and the `id` parameter.
    //
    // Namespace is Flower.Logging, not Flower.Core.Logging, matching the rest
    // of this project. It originally had to be: a Flower.Core namespace
    // shadowed LibVLCSharp's Core from inside Flower.* and broke
    // Core.Initialize at every call site.
    public static class LogPath
    {
        public static string Short(string? path)
        {
            if (string.IsNullOrEmpty(path))
                return "(none)";

            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || uri.IsFile)
                return path;

            var id = IdParameter(uri.Query);
            return id == null
                ? $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}"
                : $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath}?id={id}";
        }

        // Deliberately hand-rolled rather than HttpUtility/QueryHelpers: this
        // is on a logging path in the shared library, and pulling a web stack
        // in for one lookup is not worth it.
        private static string? IdParameter(string query)
        {
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator > 0 && pair.AsSpan(0, separator).SequenceEqual("id"))
                    return pair[(separator + 1)..];
            }

            return null;
        }
    }
}
