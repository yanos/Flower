using System.Collections.Generic;
using System.Threading.Tasks;

using Flower.Logging;

namespace Flower.Services;

// How the Log window reads a log that is not this device's own: the paired
// server's, and the last snapshot each device paired with that server pushed to
// it (see ClientLogStore). An interface rather than the concrete
// PairedServerAdminAccess so the window's own logic - which row shows what,
// what a stale response is allowed to overwrite - can be tested without an
// HTTP stack.
//
// Every failure is a return value here rather than an exception. Reading
// somebody else's log is an extra that is often simply not available (nothing
// paired, server asleep, this device not an admin of it), and a pane that says
// so is the right answer to all of those - none is exceptional enough to throw
// about.
public interface IRemoteLogSource
{
    // This device's own fingerprint, so the roster can leave out the row that
    // is just itself - "This Device" already shows that log live, and the
    // server's copy of it is always the staler one.
    string OwnFingerprint { get; }

    // Null when there is nothing to ask: no paired server, or it could not be
    // reached. Distinct from an empty list, which means a server that answered
    // and has no devices on file.
    Task<IReadOnlyList<RemoteDevice>?> ListDevicesAsync();

    // The paired server's own log.
    Task<RemoteLogResult> GetServerLogAsync(int limit);

    // One paired device's last pushed snapshot.
    Task<RemoteLogResult> GetDeviceLogAsync(string fingerprint, int limit);
}

// Why a pane is empty, when it is - each of these has a different thing to tell
// the reader, and collapsing them into one blank pane is how "the server is
// down" gets mistaken for "that phone has been quiet".
public enum RemoteLogOutcome
{
    Ok,

    // The device is on the roster but has never pushed: it has not synced since
    // the server last started, or it has log sharing switched off.
    NoSnapshot,

    // Nothing paired, unreachable, or this device is not an administrator of
    // that server - which the server decides, and says so with a 403.
    Unavailable,
}

public sealed record RemoteLogResult(RemoteLogOutcome Outcome, IReadOnlyList<InMemoryLogEntry> Entries)
{
    public static readonly RemoteLogResult Unavailable = new(RemoteLogOutcome.Unavailable, []);
    public static readonly RemoteLogResult NoSnapshot = new(RemoteLogOutcome.NoSnapshot, []);

    public static RemoteLogResult Ok(IReadOnlyList<InMemoryLogEntry> entries) => new(RemoteLogOutcome.Ok, entries);
}
