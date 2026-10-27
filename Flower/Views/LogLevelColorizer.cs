using System;
using System.Collections.Generic;

using Avalonia.Media;

using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Flower.Views;

// Colours a log line the way InMemoryLogEntry.ToDisplayLine lays it out -
// "12:22:27.854 [INF] Flower.Sync: message" - in the log viewer's text
// editor: the time muted, the level's tag on its own tint (the theme's
// AppLog* brushes), the source in the value colour, and a grouped line's
// "×N · since" muted after the message. Lines that do not start that way -
// an exception's stack, a placeholder - are left as they are.
public sealed class LogLevelColorizer : DocumentColorizingTransformer
{
    // What the viewer hands in, from the theme in use; a line is drawn plain
    // until it has them.
    public sealed record Palette(
        IBrush Muted, IBrush Value,
        IBrush DebugBackground, IBrush DebugText,
        IBrush InfoBackground, IBrush InfoText,
        IBrush WarningBackground, IBrush WarningText,
        IBrush ErrorBackground, IBrush ErrorText);

    public Palette? Colours { get; set; }

    // "HH:mm:ss.fff " then "[XXX]".
    private const int TimeLength = 12;
    private const int TagStart = 13;
    private const int TagLength = 5;

    protected override void ColorizeLine(DocumentLine line)
    {
        if (Colours is not { } colours || line.Length < TagStart + TagLength)
            return;

        var text = CurrentContext.Document.GetText(line);
        if (text[2] != ':' || text[8] != '.' || text[TagStart] != '[' || text[TagStart + TagLength - 1] != ']')
            return;

        var start = line.Offset;
        ChangeLinePart(start, start + TimeLength, e => e.TextRunProperties.SetForegroundBrush(colours.Muted));

        var (background, foreground) = text.Substring(TagStart + 1, 3) switch
        {
            "INF" => (colours.InfoBackground, colours.InfoText),
            "WAR" => (colours.WarningBackground, colours.WarningText),
            "ERR" or "FAT" => (colours.ErrorBackground, colours.ErrorText),
            _ => (colours.DebugBackground, colours.DebugText),
        };
        ChangeLinePart(start + TagStart, start + TagStart + TagLength, e =>
        {
            e.TextRunProperties.SetBackgroundBrush(background);
            e.TextRunProperties.SetForegroundBrush(foreground);
        });

        var sourceEnd = text.IndexOf(": ", TagStart + TagLength, StringComparison.Ordinal);
        if (sourceEnd > TagStart + TagLength + 1)
            ChangeLinePart(start + TagStart + TagLength + 1, start + sourceEnd, e => e.TextRunProperties.SetForegroundBrush(colours.Value));

        var repeats = text.LastIndexOf("  ×", StringComparison.Ordinal);
        if (repeats > 0 && text.IndexOf(" · since ", repeats, StringComparison.Ordinal) > 0)
            ChangeLinePart(start + repeats, line.EndOffset, e => e.TextRunProperties.SetForegroundBrush(colours.Muted));
    }
}
