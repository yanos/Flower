namespace Flower.ViewModels;

// Whether one of the server's settings can be changed from the settings page,
// and if not, why - see SettingsSnapshot.Overridden. A field binds IsEnabled to
// IsEditable and shows Message under itself while IsLocked.
public sealed class SettingLock(string? source)
{
    public static readonly SettingLock None = new(null);

    public bool IsLocked => source is not null;

    public bool IsEditable => source is null;

    public string Message => source is null
        ? ""
        : $"Set by {source}, which takes precedence over this page. Change it there instead.";
}
