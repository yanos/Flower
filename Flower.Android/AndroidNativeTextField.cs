using System;

using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Text;
using Android.Util;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

using Avalonia.Controls;
using Avalonia.Input.TextInput;

using Flower.Services;

using AColor = Android.Graphics.Color;
using AvColor = Avalonia.Media.Color;
using AvFontWeight = Avalonia.Media.FontWeight;
using AvRect = Avalonia.Rect;
using AvTextAlignment = Avalonia.Media.TextAlignment;
using View = Android.Views.View;

namespace Flower.Android;

// Android's own EditText laid over the box being typed in - see
// INativeTextFieldFactory for why, and Flower.Controls.NativeTextSession for
// how a box drives it - inside a FrameLayout that clips it, so the field
// shows only as much of itself as the box's list does of the box.
//
// Both go onto Avalonia's own view (the top level's platform handle), placed
// in pixels: Avalonia measures in device-independent units, and the top
// level's render scaling is the step between the two.
public sealed class AndroidNativeTextFieldFactory : INativeTextFieldFactory
{
    public INativeTextField? Create(TopLevel topLevel, NativeTextFieldLook look)
    {
        if (topLevel.TryGetPlatformHandle() is not { } handle
            || Java.Lang.Object.GetObject<View>(handle.Handle, JniHandleOwnership.DoNotTransfer) is not { } host)
            return null;
        // Onto the view itself where it can hold children, beside it
        // otherwise - at the same origin either way, since it fills the
        // window.
        var parent = host as ViewGroup ?? host.Parent as ViewGroup;
        if (parent is null || host.Context is not { } context)
            return null;
        return new Field(context, parent, look, topLevel.RenderScaling);
    }

    private sealed class Field : Java.Lang.Object, INativeTextField, TextView.IOnEditorActionListener, ITextWatcher, View.IOnFocusChangeListener
    {
        private readonly FrameLayout _clip;
        private readonly EditText _edit;
        private readonly NativeTextFieldLook _look;
        private readonly double _scaling;
        private bool _writing;
        private (int Width, int Height) _clipSize;
        private (int Width, int Height) _editSize;

        public Field(Context context, ViewGroup parent, NativeTextFieldLook look, double scaling)
        {
            _look = look;
            _scaling = scaling;
            _clip = new FrameLayout(context) { Visibility = ViewStates.Gone };
            _clip.SetClipChildren(true);
            _edit = new EditText(context);
            _edit.Background = null;
            _edit.SetPadding(0, 0, 0, 0);
            _edit.SetIncludeFontPadding(false);
            _edit.SetTextSize(ComplexUnitType.Px, (float)(look.FontSize * scaling));
            // The box's weight where the platform can say it (API 28), bold
            // or not before then.
            _edit.Typeface = OperatingSystem.IsAndroidVersionAtLeast(28)
                ? Typeface.Create(Typeface.Default, Math.Clamp((int)look.FontWeight, 1, 1000), false)
                : Typeface.Create(Typeface.Default, (int)look.FontWeight >= 600 ? TypefaceStyle.Bold : TypefaceStyle.Normal);
            _edit.SetTextColor(ColorOf(look.Foreground));
            _edit.SetHintTextColor(ColorOf(look.PlaceholderForeground));
            _edit.Hint = look.Placeholder;
            _edit.Gravity = (look.Multiline ? GravityFlags.Top : GravityFlags.CenterVertical) | HorizontalGravity(look.Alignment);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                // A caret in the box's caret colour rather than the theme's.
                if (_edit.TextCursorDrawable?.Mutate() is { } caret)
                {
                    caret.SetTint(ColorOf(look.Caret));
                    _edit.TextCursorDrawable = caret;
                }
            }
            _edit.InputType = InputTypeOf(look);
            if (look.Multiline)
            {
                _edit.SetSingleLine(false);
                _edit.SetHorizontallyScrolling(false);
            }
            else
            {
                _edit.SetSingleLine(true);
                _edit.ImeOptions = ImeActionOf(look.ReturnKeyType);
            }
            if (look.MaxLength > 0)
                _edit.SetFilters([new InputFilterLengthFilter(look.MaxLength)]);

            _edit.SetOnEditorActionListener(this);
            _edit.AddTextChangedListener(this);
            _edit.OnFocusChangeListener = this;

            _clip.AddView(_edit, new FrameLayout.LayoutParams(0, 0));
            parent.AddView(_clip, new ViewGroup.LayoutParams(0, 0));
        }

        public event Action<string>? TextChanged;
        public event Action? ReturnPressed;
        public event Action? EditingEnded;

        public string Text
        {
            get => _edit.Text ?? "";
            set
            {
                if (Text == value)
                    return;
                _writing = true;
                _edit.Text = value;
                _writing = false;
            }
        }

        public void Select(int start, int end)
        {
            var length = Text.Length;
            start = Math.Clamp(start, 0, length);
            _edit.SetSelection(start, Math.Clamp(end, start, length));
        }

        public void Place(AvRect field, AvRect visible)
        {
            if (visible.Width <= 0 || visible.Height <= 0)
            {
                _clip.Visibility = ViewStates.Gone;
                return;
            }

            // Sizes through layout, and only when they change; positions as
            // translations, which move a view without a layout pass, since
            // these change every frame of a scroll or a slide.
            var clipSize = (Px(visible.Width), Px(visible.Height));
            if (clipSize != _clipSize)
            {
                _clipSize = clipSize;
                _clip.LayoutParameters = new ViewGroup.LayoutParams(clipSize.Item1, clipSize.Item2);
            }
            var editSize = (Px(field.Width), Px(field.Height));
            if (editSize != _editSize)
            {
                _editSize = editSize;
                _edit.LayoutParameters = new FrameLayout.LayoutParams(editSize.Item1, editSize.Item2);
            }
            _clip.TranslationX = Px(visible.X);
            _clip.TranslationY = Px(visible.Y);
            _edit.TranslationX = Px(field.X - visible.X);
            _edit.TranslationY = Px(field.Y - visible.Y);
            _clip.Visibility = ViewStates.Visible;
        }

        public void BeginEditing()
        {
            _edit.RequestFocus();
            Keyboard?.ShowSoftInput(_edit, ShowFlags.Implicit);
        }

        public new void Dispose()
        {
            // Still focused means nothing took the keyboard from it on the way
            // out, so it is put away with the field; otherwise it is the next
            // field's now and stays.
            if (_edit.HasFocus)
            {
                _edit.ClearFocus();
                Keyboard?.HideSoftInputFromWindow(_edit.WindowToken, HideSoftInputFlags.None);
            }
            _edit.RemoveTextChangedListener(this);
            _edit.SetOnEditorActionListener(null);
            _edit.OnFocusChangeListener = null;
            (_clip.Parent as ViewGroup)?.RemoveView(_clip);
            _edit.Dispose();
            _clip.Dispose();
            base.Dispose();
        }

        private InputMethodManager? Keyboard =>
            _edit.Context?.GetSystemService(Context.InputMethodService) as InputMethodManager;

        private int Px(double dip) => (int)Math.Round(dip * _scaling);

        // A one-line field's Return: the box's Enter.
        public bool OnEditorAction(TextView? v, ImeAction actionId, KeyEvent? e)
        {
            if (_look.Multiline)
                return false;
            ReturnPressed?.Invoke();
            return true;
        }

        public void AfterTextChanged(IEditable? s)
        {
            if (!_writing)
                TextChanged?.Invoke(s?.ToString() ?? "");
        }

        public void BeforeTextChanged(Java.Lang.ICharSequence? s, int start, int count, int after)
        {
        }

        public void OnTextChanged(Java.Lang.ICharSequence? s, int start, int before, int count)
        {
        }

        public void OnFocusChange(View? v, bool hasFocus)
        {
            if (!hasFocus)
                EditingEnded?.Invoke();
        }

        private static AColor ColorOf(AvColor c) => new(c.R, c.G, c.B, c.A);

        private static GravityFlags HorizontalGravity(AvTextAlignment alignment) => alignment switch
        {
            AvTextAlignment.Center => GravityFlags.CenterHorizontal,
            AvTextAlignment.Right or AvTextAlignment.End => GravityFlags.End,
            _ => GravityFlags.Start,
        };

        private static ImeAction ImeActionOf(TextInputReturnKeyType key) => key switch
        {
            TextInputReturnKeyType.Go => ImeAction.Go,
            TextInputReturnKeyType.Send => ImeAction.Send,
            TextInputReturnKeyType.Search => ImeAction.Search,
            TextInputReturnKeyType.Next => ImeAction.Next,
            TextInputReturnKeyType.Previous => ImeAction.Previous,
            _ => ImeAction.Done,
        };

        // The keyboard the box asked for (TextInputOptions), as an input type.
        private static InputTypes InputTypeOf(NativeTextFieldLook look)
        {
            var type = look.ContentType switch
            {
                TextInputContentType.Digits or TextInputContentType.Pin => InputTypes.ClassNumber,
                TextInputContentType.Number => InputTypes.ClassNumber | InputTypes.NumberFlagDecimal | InputTypes.NumberFlagSigned,
                TextInputContentType.Email => InputTypes.ClassText | InputTypes.TextVariationEmailAddress,
                TextInputContentType.Url => InputTypes.ClassText | InputTypes.TextVariationUri,
                TextInputContentType.Password => InputTypes.ClassText | InputTypes.TextVariationPassword,
                TextInputContentType.Name => InputTypes.ClassText | InputTypes.TextVariationPersonName | InputTypes.TextFlagCapWords,
                _ => InputTypes.ClassText,
            };
            if ((type & InputTypes.MaskClass) != InputTypes.ClassText)
                return type;
            if (look.Multiline)
                type |= InputTypes.TextFlagMultiLine;
            if (look.AutoCapitalization != false && look.ContentType is TextInputContentType.Normal or TextInputContentType.Alpha or TextInputContentType.Search or TextInputContentType.Social)
                type |= InputTypes.TextFlagCapSentences;
            if (look.ShowSuggestions == false)
                type |= InputTypes.TextFlagNoSuggestions;
            return type;
        }
    }
}
