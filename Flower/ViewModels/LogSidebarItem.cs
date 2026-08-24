namespace Flower.ViewModels;

public enum LogSidebarItemKind { ThisDevice, Server, PairedClient }

// Row shape for the Log window's sidebar (see LogViewModel/LogWindow) -
// deliberately not SidebarItem: that class models the library sidebar's much
// richer set of states (editing, drag targets, live-syncing indicator) none
// of which apply here, just "which instance's log am I looking at."
public class LogSidebarItem
{
    public LogSidebarItemKind Kind { get; }
    public string Name { get; }

    // Only PairedClient rows carry one - ThisDevice reads a local buffer, and
    // Server asks its own admin route rather than one keyed by fingerprint.
    public string? Fingerprint { get; }

    public LogSidebarItem(LogSidebarItemKind kind, string name, string? fingerprint = null)
    {
        Kind = kind;
        Name = name;
        Fingerprint = fingerprint;
    }
}
