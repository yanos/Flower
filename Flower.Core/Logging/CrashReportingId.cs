using System;
using System.IO;

using Flower.Persistence;

namespace Flower.Logging
{
    // A random identity for this install's crash reports, so Sentry can say
    // how many installs a problem reaches - its "users". Made from nothing:
    // not the machine, not the person, not the paired server, so it says
    // nothing about who or where. Created the first time a report needs it,
    // and thrown away when reporting is switched off, so switching it back on
    // starts a stranger.
    public static class CrashReportingId
    {
        private static readonly object Gate = new();

        public static string FilePath => Path.Combine(AppDataDirectory.Path, "crash-reporting-id");

        public static string? Current
        {
            get
            {
                lock (Gate)
                {
                    try
                    {
                        if (File.Exists(FilePath) && Guid.TryParse(File.ReadAllText(FilePath).Trim(), out var existing))
                            return existing.ToString("N");

                        var created = Guid.NewGuid().ToString("N");
                        Directory.CreateDirectory(AppDataDirectory.Path);
                        File.WriteAllText(FilePath, created);
                        return created;
                    }
                    catch
                    {
                        // Unwritable: a report with no install counts as none.
                        return null;
                    }
                }
            }
        }

        public static void Forget()
        {
            lock (Gate)
            {
                try
                {
                    File.Delete(FilePath);
                }
                catch
                {
                    // Best effort - at worst the next report counts as the same install.
                }
            }
        }
    }
}
