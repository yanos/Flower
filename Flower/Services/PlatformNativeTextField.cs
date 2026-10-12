using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.TextInput;
using Avalonia.Media;

namespace Flower.Services
{
    // The platform's own text field, laid over one of the app's text boxes
    // while it is being typed in (Flower.Controls.TextInput, NativeTextSession).
    // At rest the box is Avalonia's, so it scrolls, slides and sits under the
    // floating ovals like everything else drawn; while it is edited, the
    // typing, the caret, selection and its handles, the magnifier, autocorrect,
    // dictation and the keyboard's own suggestions are the OS's.
    //
    // A field the app positions itself, every frame, as a subview of the
    // app's own native view - not a NativeControlHost, which puts its view
    // where layout last put the control and so leaves it behind a sliding
    // sheet or a scroll.
    //
    // Set by a platform head that has one (Flower.iOS, Flower.Android) before
    // Avalonia starts; everywhere else Factory stays null and every box is
    // Avalonia's throughout.
    public interface INativeTextFieldFactory
    {
        /// <summary>
        /// A field over <paramref name="topLevel"/>'s native view, hidden until
        /// first placed. Null if the platform cannot make one there.
        /// </summary>
        INativeTextField? Create(TopLevel topLevel, NativeTextFieldLook look);
    }

    public interface INativeTextField : IDisposable
    {
        /// <summary>The field's text. Setting it raises no TextChanged.</summary>
        string Text { get; set; }

        /// <summary>Selects from start to end (equal for a caret), in characters.</summary>
        void Select(int start, int end);

        /// <summary>
        /// Puts the field over <paramref name="field"/> and shows only the part
        /// of it inside <paramref name="visible"/> - both in the top level's
        /// device-independent coordinates. An empty visible rectangle hides it.
        /// </summary>
        void Place(Rect field, Rect visible);

        /// <summary>Takes the keyboard: the field becomes the one typed in.</summary>
        void BeginEditing();

        /// <summary>Raised as the user types, with the field's whole text.</summary>
        event Action<string>? TextChanged;

        /// <summary>Raised by a single-line field's Return key.</summary>
        event Action? ReturnPressed;

        /// <summary>
        /// Raised when the field stops being typed in without being told to -
        /// the platform taking the keyboard away from it.
        /// </summary>
        event Action? EditingEnded;
    }

    // How the box being edited looks and what it is for, so the field laid
    // over it draws its text the same size, weight, colour and alignment, and
    // asks for the right keyboard.
    public sealed record NativeTextFieldLook(
        double FontSize,
        FontWeight FontWeight,
        Color Foreground,
        Color PlaceholderForeground,
        Color Caret,
        string? Placeholder,
        TextAlignment Alignment,
        bool Multiline,
        int MaxLength,
        TextInputContentType ContentType,
        TextInputReturnKeyType ReturnKeyType,
        bool? AutoCapitalization,
        bool? ShowSuggestions,
        bool IsSensitive);

    public static class PlatformNativeTextField
    {
        public static INativeTextFieldFactory? Factory { get; set; }
    }
}
