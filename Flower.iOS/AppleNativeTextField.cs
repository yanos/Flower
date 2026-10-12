using System;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.TextInput;
using Avalonia.Media;

using CoreGraphics;

using Foundation;

using ObjCRuntime;

using UIKit;

using Flower.Services;

namespace Flower.iOS;

// UIKit's own text field laid over the box being typed in - see
// INativeTextFieldFactory for why, and Flower.Controls.NativeTextSession for
// how a box drives it. A UITextField for a one-line box and a UITextView for
// one that takes Return, each inside a plain view that clips it, so the field
// shows only as much of itself as the box's list does of the box.
//
// Both go straight onto Avalonia's own UIView (the top level's platform
// handle), whose coordinates are Avalonia's device-independent ones: a point
// on iOS is what Avalonia measures in.
public sealed class AppleNativeTextFieldFactory : INativeTextFieldFactory
{
    public INativeTextField? Create(TopLevel topLevel, NativeTextFieldLook look)
    {
        if (topLevel.TryGetPlatformHandle() is not { } handle
            || Runtime.GetNSObject<UIView>(handle.Handle) is not { } host)
            return null;
        return look.Multiline ? new MultiLine(host, look) : new SingleLine(host, look);
    }

    // What both kinds share: the clipping view, placing, and the look's
    // colours, font and keyboard.
    private abstract class Field : INativeTextField
    {
        private readonly UIView _clip;
        protected readonly NativeTextFieldLook Look;
        protected bool Writing;
        private (int Start, int End)? _pendingSelection;

        protected Field(UIView host, UIView field, NativeTextFieldLook look)
        {
            Look = look;
            _clip = new UIView { ClipsToBounds = true, Hidden = true, BackgroundColor = UIColor.Clear };
            _clip.AddSubview(field);
            host.AddSubview(_clip);
        }

        protected abstract UIView View { get; }
        public abstract string Text { get; set; }

        public event Action<string>? TextChanged;
        public event Action? ReturnPressed;
        public event Action? EditingEnded;

        protected void RaiseTextChanged(string text)
        {
            if (!Writing)
                TextChanged?.Invoke(text);
        }

        protected void RaiseReturn() => ReturnPressed?.Invoke();
        protected void RaiseEditingEnded() => EditingEnded?.Invoke();

        public void Place(Rect field, Rect visible)
        {
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                _clip.Hidden = true;
                return;
            }
            _clip.Frame = new CGRect(visible.X, visible.Y, visible.Width, visible.Height);
            View.Frame = new CGRect(field.X - visible.X, field.Y - visible.Y, field.Width, field.Height);
            _clip.Hidden = false;
            Placed();
        }

        protected virtual void Placed()
        {
        }

        // A selection only holds once the field is first responder, so one
        // asked for before then waits for BeginEditing.
        public void Select(int start, int end)
        {
            if (View.IsFirstResponder)
                ApplySelection(start, end);
            else
                _pendingSelection = (start, end);
        }

        public void BeginEditing()
        {
            View.BecomeFirstResponder();
            if (_pendingSelection is { } selection)
                ApplySelection(selection.Start, selection.End);
            _pendingSelection = null;
        }

        private void ApplySelection(int start, int end)
        {
            if (View is not IUITextInput input)
                return;
            var length = Text.Length;
            start = Math.Clamp(start, 0, length);
            end = Math.Clamp(end, start, length);
            var from = input.GetPosition(input.BeginningOfDocument, start);
            var to = input.GetPosition(input.BeginningOfDocument, end);
            if (from != null && to != null)
                input.SelectedTextRange = input.GetTextRange(from, to);
        }

        // Within the box's maximum, if it has one: a change that would go
        // past it is refused whole, as Avalonia's box refuses it.
        protected bool Allows(nint location, nint length, string replacement)
        {
            if (Look.MaxLength <= 0)
                return true;
            return Text.Length - (int)length + replacement.Length <= Look.MaxLength;
        }

        public void Dispose()
        {
            View.ResignFirstResponder();
            _clip.RemoveFromSuperview();
            _clip.Dispose();
        }

        protected UIFont Font =>
            UIFont.SystemFontOfSize((nfloat)Look.FontSize, WeightOf(Look.FontWeight)) ?? UIFont.SystemFontOfSize((nfloat)Look.FontSize);

        protected static UIColor ColorOf(Color c) =>
            UIColor.FromRGBA(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

        protected UITextAlignment Alignment => Look.Alignment switch
        {
            TextAlignment.Center => UITextAlignment.Center,
            TextAlignment.Right or TextAlignment.End => UITextAlignment.Right,
            TextAlignment.Justify => UITextAlignment.Justified,
            _ => UITextAlignment.Natural,
        };

        // The keyboard the box asked for (TextInputOptions), as UIKit's traits.
        protected void ApplyTraits(IUITextInputTraits traits)
        {
            traits.KeyboardType = Look.ContentType switch
            {
                TextInputContentType.Digits or TextInputContentType.Pin => UIKeyboardType.NumberPad,
                TextInputContentType.Number => UIKeyboardType.DecimalPad,
                TextInputContentType.Email => UIKeyboardType.EmailAddress,
                TextInputContentType.Url => UIKeyboardType.Url,
                TextInputContentType.Search => UIKeyboardType.WebSearch,
                TextInputContentType.Social => UIKeyboardType.Twitter,
                _ => UIKeyboardType.Default,
            };
            traits.AutocapitalizationType = Look.AutoCapitalization switch
            {
                false => UITextAutocapitalizationType.None,
                _ when Look.ContentType is TextInputContentType.Email or TextInputContentType.Url
                    or TextInputContentType.Password => UITextAutocapitalizationType.None,
                _ when Look.ContentType == TextInputContentType.Name => UITextAutocapitalizationType.Words,
                _ => UITextAutocapitalizationType.Sentences,
            };
            if (Look.ShowSuggestions == false || Look.ContentType is TextInputContentType.Password or TextInputContentType.Email or TextInputContentType.Url)
            {
                traits.AutocorrectionType = UITextAutocorrectionType.No;
                traits.SpellCheckingType = UITextSpellCheckingType.No;
            }
            traits.SecureTextEntry = Look.ContentType == TextInputContentType.Password;
        }

        private static UIFontWeight WeightOf(FontWeight weight) => (int)weight switch
        {
            <= 150 => UIFontWeight.Thin,
            <= 250 => UIFontWeight.UltraLight,
            <= 350 => UIFontWeight.Light,
            <= 450 => UIFontWeight.Regular,
            <= 550 => UIFontWeight.Medium,
            <= 650 => UIFontWeight.Semibold,
            <= 750 => UIFontWeight.Bold,
            <= 850 => UIFontWeight.Heavy,
            _ => UIFontWeight.Black,
        };
    }

    private sealed class SingleLine : Field
    {
        private readonly UITextField _field;

        public SingleLine(UIView host, NativeTextFieldLook look)
            : this(host, new UITextField(), look)
        {
        }

        private SingleLine(UIView host, UITextField field, NativeTextFieldLook look)
            : base(host, field, look)
        {
            _field = field;
            field.BorderStyle = UITextBorderStyle.None;
            field.BackgroundColor = UIColor.Clear;
            field.Font = Font;
            field.TextColor = ColorOf(look.Foreground);
            field.TintColor = ColorOf(look.Caret);
            field.TextAlignment = Alignment;
            field.VerticalAlignment = UIControlContentVerticalAlignment.Center;
            if (!string.IsNullOrEmpty(look.Placeholder))
                field.AttributedPlaceholder = new NSAttributedString(look.Placeholder,
                    new UIStringAttributes { ForegroundColor = ColorOf(look.PlaceholderForeground), Font = Font });
            ApplyTraits(field);
            // A one-line box's Return is "done" on a phone, unless the box
            // asked for another key.
            field.ReturnKeyType = look.ReturnKeyType switch
            {
                TextInputReturnKeyType.Return => UIReturnKeyType.Default,
                TextInputReturnKeyType.Go => UIReturnKeyType.Go,
                TextInputReturnKeyType.Send => UIReturnKeyType.Send,
                TextInputReturnKeyType.Search => UIReturnKeyType.Search,
                TextInputReturnKeyType.Next => UIReturnKeyType.Next,
                _ => UIReturnKeyType.Done,
            };

            field.EditingChanged += (_, _) => RaiseTextChanged(_field.Text ?? "");
            field.EditingDidEnd += (_, _) => RaiseEditingEnded();
            field.ShouldReturn = _ =>
            {
                RaiseReturn();
                return false;
            };
            field.ShouldChangeCharacters = (_, range, replacement) => Allows(range.Location, range.Length, replacement);
        }

        protected override UIView View => _field;

        public override string Text
        {
            get => _field.Text ?? "";
            set
            {
                if (_field.Text == value)
                    return;
                Writing = true;
                _field.Text = value;
                Writing = false;
            }
        }
    }

    // UITextView has no placeholder of its own, so the box's hint is a label
    // inside it, shown while it is empty.
    private sealed class MultiLine : Field
    {
        private readonly UITextView _view;
        private readonly UILabel _placeholder;

        public MultiLine(UIView host, NativeTextFieldLook look)
            : this(host, new UITextView(), look)
        {
        }

        private MultiLine(UIView host, UITextView view, NativeTextFieldLook look)
            : base(host, view, look)
        {
            _view = view;
            view.BackgroundColor = UIColor.Clear;
            // The box lays its text out flush with its text area, so the
            // view does too: no inset, no padding before a line.
            view.TextContainerInset = UIEdgeInsets.Zero;
            view.TextContainer.LineFragmentPadding = 0;
            // The box grows to fit what it holds, and the view with it.
            view.ScrollEnabled = false;
            view.Font = Font;
            view.TextColor = ColorOf(look.Foreground);
            view.TintColor = ColorOf(look.Caret);
            view.TextAlignment = Alignment;
            ApplyTraits(view);

            _placeholder = new UILabel
            {
                Text = look.Placeholder,
                Font = Font,
                TextColor = ColorOf(look.PlaceholderForeground),
                TextAlignment = Alignment,
                Lines = 0,
            };
            view.AddSubview(_placeholder);

            view.Changed += (_, _) =>
            {
                UpdatePlaceholder();
                RaiseTextChanged(_view.Text ?? "");
            };
            view.Ended += (_, _) => RaiseEditingEnded();
            view.ShouldChangeText = (_, range, replacement) => Allows(range.Location, range.Length, replacement);
        }

        protected override UIView View => _view;

        protected override void Placed() => UpdatePlaceholder();

        public override string Text
        {
            get => _view.Text ?? "";
            set
            {
                if (_view.Text == value)
                    return;
                Writing = true;
                _view.Text = value;
                Writing = false;
                UpdatePlaceholder();
            }
        }

        private void UpdatePlaceholder()
        {
            _placeholder.Hidden = !string.IsNullOrEmpty(_view.Text) || string.IsNullOrEmpty(Look.Placeholder);
            _placeholder.Frame = new CGRect(0, 0, _view.Bounds.Width, _placeholder.SizeThatFits(new CGSize(_view.Bounds.Width, nfloat.MaxValue)).Height);
        }
    }
}
