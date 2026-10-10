using System;
using System.Linq;

using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Controls;

using SkiaSharp;

namespace Flower.Controls;

// Blurs whatever has already been drawn under it - a backdrop filter, which
// Avalonia does not have for the app's own content. Each screen's header band
// (ScreenSlot) uses it so a list scrolled up under the clock and the back
// button goes soft there.
//
// It replaced a blurred VisualBrush copy of the screen, which was wrong twice
// over. A VisualBrush is recorded when the control painting it renders, and
// nothing about a list scrolling under it re-renders that control, so the blur
// went on showing what had been there when the screen opened - a cover long
// since scrolled away. And the compositor still drew that recording, the whole
// screen, offscreen and blurred, on every frame anything under the band moved,
// which is every frame of a scroll.
//
// This draws on the render thread, inside the compositor's own pass: a Skia
// layer whose backdrop is the pixels already drawn beneath it, blurred, then
// faded out down the control by Mask. So it is exactly as current as the frame
// it is in, and costs one blur of its own small area. Where there is no Skia
// (the headless test renderer) it draws nothing, and what is under it shows
// through unblurred.
public class BackdropBlur : Control
{
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<BackdropBlur, double>(nameof(Radius), 18);

    /// <summary>How soft the blur is, measured like BlurEffect's Radius.</summary>
    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public static readonly StyledProperty<IGradientBrush?> MaskProperty =
        AvaloniaProperty.Register<BackdropBlur, IGradientBrush?>(nameof(Mask));

    /// <summary>
    /// How much of the blur shows, down the control from its top: only the
    /// stops' alpha and offsets count, and they always run top to bottom.
    /// Without one the blur is all there, everywhere.
    /// </summary>
    public IGradientBrush? Mask
    {
        get => GetValue(MaskProperty);
        set => SetValue(MaskProperty, value);
    }

    static BackdropBlur()
    {
        AffectsRender<BackdropBlur>(RadiusProperty, MaskProperty);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || Radius <= 0)
            return;
        // Read here, on the UI thread: the operation itself runs on the
        // render thread, where the brush is not ours to touch.
        var stops = Mask?.GradientStops.OrderBy(s => s.Offset).ToArray();
        context.Custom(new BlurOperation(
            new Rect(Bounds.Size),
            Radius,
            stops?.Select(s => s.Color.A / 255f).ToArray(),
            stops?.Select(s => (float)s.Offset).ToArray()));
    }

    private sealed class BlurOperation(Rect bounds, double radius, float[]? alphas, float[]? offsets) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => false;

        // Never equal, even to an identical one: what it draws depends on
        // what is under it, which this cannot see, so the compositor must not
        // skip it as unchanged.
        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var rect = new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height);
            // BlurEffect's own radius-to-sigma (Avalonia.Skia's
            // SkBlurRadiusToSigma), so a Radius here is as soft as it was
            // there. The filter is in this control's coordinates; Skia scales
            // it with the canvas to the screen's pixels.
            var sigma = 0.288675f * (float)radius + 0.5f;

            canvas.Save();
            canvas.ClipRect(rect);
            using var blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
            canvas.SaveLayer(new SKCanvasSaveLayerRec { Bounds = rect, Backdrop = blur });
            // Keeps the layer - the blurred backdrop - only as much as the mask
            // says at each height, so restoring it lays the blur over the
            // sharp original by that much.
            var colors = (alphas is { Length: > 1 } ? alphas : [1f, 1f])
                .Select(a => new SKColor(0, 0, 0, (byte)Math.Round(a * lease.CurrentOpacity * 255)))
                .ToArray();
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, rect.Height), colors,
                alphas is { Length: > 1 } ? offsets : [0f, 1f], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader, BlendMode = SKBlendMode.DstIn };
            canvas.DrawRect(rect, paint);
            canvas.Restore();
            canvas.Restore();
        }
    }
}
