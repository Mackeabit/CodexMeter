using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using W = System.Windows;
using WM = System.Windows.Media;
using WI = System.Windows.Media.Imaging;

namespace CodexMeter;

/// <summary>
/// Meter text drawn with WPF's text stack instead of GDI+. GDI+ grayscale text on the meter's transparent surface loses
/// much of its coverage: on a 100% Windows 10 desktop the 12 px Hangul rows came out dim and smeared, and the ring digits
/// bled into the inner ring. WPF in Display mode hints glyphs to the pixel grid at full strength. Each string is
/// rasterized at device pixels and placed on whole pixels, so nothing is resampled afterwards.
/// Must run on a WPF (STA) thread, like the rest of the meter's drawing.
/// </summary>
internal static class TextArt
{
    internal enum Face { Number, Text }

    // Segoe UI Variable ships with Windows 11; Windows 10 gets Segoe UI at the same (semibold) weight.
    private static readonly Lazy<WM.Typeface> number = new(() =>
        Find("Segoe UI Variable Display", W.FontWeights.SemiBold) ?? new WM.Typeface(new WM.FontFamily("Segoe UI"), W.FontStyles.Normal, W.FontWeights.SemiBold, W.FontStretches.Normal));
    private static readonly Lazy<WM.Typeface> text = new(() => new WM.Typeface(new WM.FontFamily(RingArtwork.TokenFont), W.FontStyles.Normal, W.FontWeights.Normal, W.FontStretches.Normal));

    internal static WM.Typeface Typeface(Face face) => face == Face.Number ? number.Value : text.Value;
    internal static string NumberFamily => number.Value.FontFamily.Source;

    // Matched against the installed family list: WPF resolves an unknown family to a fallback font instead of failing, and
    // Segoe UI Variable's glyph typeface reports its family as "Segoe UI Variable" (the instance is in the face name,
    // "Display Semibold"), so neither TryGetGlyphTypeface nor the glyph's family name tells whether it is installed.
    private static WM.Typeface? Find(string family, W.FontWeight weight) =>
        System.Linq.Enumerable.Any(WM.Fonts.SystemFontFamilies, f => string.Equals(f.Source, family, StringComparison.OrdinalIgnoreCase))
            ? new WM.Typeface(new WM.FontFamily(family), W.FontStyles.Normal, weight, W.FontStretches.Normal) : null;

    private static WM.FormattedText Format(string value, Face face, double pixels, Color color) =>
        new(value, CultureInfo.GetCultureInfo("ko-KR"), W.FlowDirection.LeftToRight, Typeface(face), pixels,
            new WM.SolidColorBrush(WM.Color.FromArgb(color.A, color.R, color.G, color.B)), null, WM.TextFormattingMode.Display, 1.0);

    /// <summary>Advance width in DIPs at a given scale (Display mode rounds per scale, so callers check the scales they ship).</summary>
    internal static double Width(string value, Face face, float sizeDip, float scale) =>
        Format(value, face, sizeDip * scale, Color.Black).WidthIncludingTrailingWhitespace / scale;

    /// <summary>Ink bounds in DIPs, relative to the text origin (top-left of the line box).</summary>
    internal static W.Rect Ink(string value, Face face, float sizeDip, float scale)
    {
        var bounds = Format(value, face, sizeDip * scale, Color.Black).BuildGeometry(new W.Point(0, 0)).Bounds;
        return bounds.IsEmpty ? W.Rect.Empty : new W.Rect(bounds.X / scale, bounds.Y / scale, bounds.Width / scale, bounds.Height / scale);
    }

    /// <summary>Line text in a DIP rectangle: horizontally near/far/center, vertically centered on the line box.</summary>
    internal static void Draw(Graphics g, float scale, string value, Face face, float sizeDip, Color color, RectangleF rect, StringAlignment align = StringAlignment.Near)
    {
        if (value.Length == 0) return;
        var ft = Format(value, face, sizeDip * scale, color);
        double left = rect.Left * scale, right = rect.Right * scale;
        double x = align switch
        {
            StringAlignment.Far => right - ft.WidthIncludingTrailingWhitespace,
            StringAlignment.Center => (left + right - ft.WidthIncludingTrailingWhitespace) / 2,
            _ => left,
        };
        double y = rect.Top * scale + (rect.Height * scale - ft.Height) / 2;
        Blit(g, ft, x, y);
    }

    /// <summary>
    /// A ring's center number: its ink box centered on (cx, cy), shrunk until it keeps one device pixel clear of a
    /// circular opening of <paramref name="opening"/> DIPs on every side.
    /// </summary>
    internal static void DrawCentered(Graphics g, float scale, string value, float sizeDip, Color color, float cx, float cy, float opening)
    {
        float size = FitSize(value, sizeDip, scale, opening);
        var ft = Format(value, Face.Number, size * scale, color);
        var ink = ft.BuildGeometry(new W.Point(0, 0)).Bounds;
        if (ink.IsEmpty) return;
        Blit(g, ft, cx * scale - (ink.Left + ink.Width / 2), cy * scale - (ink.Top + ink.Height / 2));
    }

    /// <summary>Largest size up to <paramref name="sizeDip"/> whose ink box (its corners included) clears the opening.</summary>
    internal static float FitSize(string value, float sizeDip, float scale, float opening)
    {
        float room = opening - 2f / scale;
        for (float size = sizeDip; size > 4; size -= .25f)
        {
            var ink = Ink(value, Face.Number, size, scale);
            // The opening is round, so the full width only fits at mid-height. Digit tops and bottoms are rounded, so half
            // of the ink's height (not its full corner) is checked against the circle along with the width.
            if (ink.IsEmpty) return size;
            double hw = ink.Width / 2, hh = ink.Height / 4, r = room / 2;
            if (hw * hw + hh * hh <= r * r && ink.Height <= room) return size;
        }
        return 4;
    }

    // Rasterize at device pixels and copy 1:1 onto whole pixels of the target.
    private static void Blit(Graphics g, WM.FormattedText ft, double x, double y)
    {
        int ix = (int)Math.Round(x), iy = (int)Math.Round(y);
        const int pad = 2;
        int w = (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 2 * pad, h = (int)Math.Ceiling(ft.Height) + 2 * pad;
        var visual = new WM.DrawingVisual();
        WM.TextOptions.SetTextFormattingMode(visual, WM.TextFormattingMode.Display);
        WM.TextOptions.SetTextRenderingMode(visual, WM.TextRenderingMode.Grayscale);
        WM.TextOptions.SetTextHintingMode(visual, WM.TextHintingMode.Fixed);
        using (var dc = visual.RenderOpen()) dc.DrawText(ft, new W.Point(pad, pad));
        var target = new WI.RenderTargetBitmap(w, h, 96, 96, WM.PixelFormats.Pbgra32);
        target.Render(visual);
        using var bitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bitmap.PixelFormat);
        try { target.CopyPixels(W.Int32Rect.Empty, data.Scan0, data.Stride * h, data.Stride); }
        finally { bitmap.UnlockBits(data); }
        var transform = g.Transform;
        var interpolation = g.InterpolationMode;
        var offset = g.PixelOffsetMode;
        try
        {
            g.ResetTransform();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(bitmap, new Rectangle(ix - pad, iy - pad, w, h), 0, 0, w, h, GraphicsUnit.Pixel);
        }
        finally { g.Transform = transform; g.InterpolationMode = interpolation; g.PixelOffsetMode = offset; }
    }
}
