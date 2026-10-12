using System;
using System.Collections.Generic;

using Avalonia;
using Avalonia.Controls;

using Flower.Services;

namespace Flower.Tests.TestSupport;

// A platform text field that is only a record of what was asked of it, and
// the three things a user can do to one: type, press Return, and have the
// platform take the keyboard away.
public sealed class FakeNativeTextFieldFactory : INativeTextFieldFactory
{
    public List<FakeNativeTextField> Created { get; } = new();

    // Every field's BeginEditing and Dispose, in the order they happened.
    public List<string> Log { get; } = new();

    public INativeTextField Create(TopLevel topLevel, NativeTextFieldLook look)
    {
        var field = new FakeNativeTextField(look, Log, Created.Count);
        Created.Add(field);
        return field;
    }
}

public sealed class FakeNativeTextField(NativeTextFieldLook look, List<string> log, int index) : INativeTextField
{
    public NativeTextFieldLook Look { get; } = look;
    public string Text { get; set; } = "";
    public (int Start, int End) Selection { get; private set; }
    public bool IsEditing { get; private set; }
    public bool IsDisposed { get; private set; }

    public event Action<string>? TextChanged;
    public event Action? ReturnPressed;
    public event Action? EditingEnded;

    public void Select(int start, int end) => Selection = (start, end);

    public void Place(Rect field, Rect visible)
    {
    }

    public void BeginEditing()
    {
        IsEditing = true;
        log.Add($"begin {index}");
    }

    public void Dispose()
    {
        IsDisposed = true;
        IsEditing = false;
        log.Add($"dispose {index}");
    }

    public void Type(string text)
    {
        Text = text;
        TextChanged?.Invoke(text);
    }

    public void PressReturn() => ReturnPressed?.Invoke();

    public void LoseKeyboard()
    {
        IsEditing = false;
        EditingEnded?.Invoke();
    }
}
