using System;

using Avalonia.Input;

namespace Flower.Services;

// Central place for the "primary" keyboard shortcut modifier — Cmd on macOS,
// Ctrl on Windows/Linux. Route every shortcut through this instead of
// hardcoding KeyModifiers.Meta so it stays correct cross-platform; this is
// also the seam to swap in per-user configurable shortcuts later.
public static class PlatformShortcuts
{
    public static KeyModifiers Primary => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
}
