using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;

namespace CodexMeter;

/// <summary>One service on the taskbar: outer ring = 5-hour window, inner ring = weekly window.</summary>
internal sealed record RingService(string Short, Color Accent, Quota? FiveHour, Quota? Weekly, bool Stale, bool Warning)
{
    public static readonly Color CodexAccent = Color.FromArgb(222, 128, 94);
    public static readonly Color ClaudeAccent = Color.FromArgb(143, 134, 230);
    public Quota? Lowest => new[] { FiveHour, Weekly }.Where(q => q != null).OrderBy(q => q!.Remaining).ThenBy(q => q!.Reset ?? DateTimeOffset.MaxValue).FirstOrDefault();
    public string Center => Warning ? "!" : Lowest is Quota q ? Math.Floor(q.Remaining).ToString("0", CultureInfo.InvariantCulture) : "—";
}

internal static class RingArtwork
{
    internal const double LowThreshold = 20;
    // Layout in DIPs: [pad][ring][gap][token][service gap][ring][gap][token][pad] = 158, the Classic meter's width.
    internal const int Pad = 2;
    internal const int RingCell = 40;
    internal const int TokenGap = 3;
    internal const int TokenWidth = 31;
    internal const int ServiceGap = 6;
    internal const int RingGap = 4;
    internal const int MinimumWidth = Pad + 2 * RingCell + RingGap + Pad;
    internal const int PreferredWidth = Pad + 2 * (RingCell + TokenGap + TokenWidth) + ServiceGap + Pad;
    internal static readonly Color Low = Color.FromArgb(226, 75, 74);
    internal const string NumberFont = "Segoe UI Variable Display Semib";
    internal const string TokenFont = "Malgun Gothic";

    /// <summary>At most four characters so it fits beside a ring: 6일, 3:12, 42분.</summary>
    internal static string ShortCountdown(Quota? q, DateTimeOffset now)
    {
        if (q?.Reset is not DateTimeOffset reset) return "—";
        var span = reset - now;
        if (span.TotalSeconds <= 0) return "확인";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}일";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}:{span.Minutes:00}";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}분";
    }

    internal static string WindowName(Quota q) => q.Minutes == 300 ? "5h" : q.Minutes == 10080 ? "주" : q.Duration;

    /// <summary>Accent text needs a darker tone on a light taskbar to keep about 4.5:1 contrast.</summary>
    internal static Color TextAccent(RingService s, bool light) => !light ? s.Accent
        : s.Accent.ToArgb() == RingService.CodexAccent.ToArgb() ? Color.FromArgb(168, 78, 46) : Color.FromArgb(92, 82, 190);

    /// <summary>Uses the requested family when installed (Segoe UI Variable ships with Windows 11), otherwise Segoe UI.</summary>
    internal static Font Font(string family, float size, string fallback = "Segoe UI")
    {
        var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
        if (font.Name == family) return font;
        font.Dispose();
        return new Font(fallback, size, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    /// <summary>
    /// Draws on a transparent surface so the real taskbar material (including Mica tint) shows through.
    /// The near-zero alpha fill keeps the whole meter hit-testable for hover, tooltip and clicks.
    /// </summary>
    internal static Bitmap Render(int width, int height, float scale, RingService[] services, bool light, DateTimeOffset now, bool hover = false)
    {
        if (width + .01f < MinimumWidth * scale) throw new ArgumentOutOfRangeException(nameof(width), "Both rings need their reserved width.");
        var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(1, 0, 0, 0));
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        float w = width / scale, h = height / scale;
        if (hover)
        {
            // Windows 11 taskbar buttons: 4 DIP corner radius and a faint wash of the foreground color.
            using var wash = new SolidBrush(light ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(20, 255, 255, 255));
            using var path = RoundedRect(new RectangleF(.5f, 2, w - 1, h - 4), 4);
            g.FillPath(wash, path);
        }
        var ink = light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(242, 242, 242);
        var stale = light ? Color.FromArgb(150, 150, 150) : Color.FromArgb(128, 128, 128);
        // The outer margin is at least the space between rings, so the pair never looks cramped against the taskbar edge.
        float diameter = Math.Min(RingCell - 4, h - 8);
        float outerStroke = Math.Max(3, diameter * .1f), innerStroke = Math.Max(2.5f, diameter * .085f), ringGap = Math.Max(2f, diameter * .065f);
        float cy = h / 2;
        // Reset tokens need the preferred width; a narrower gap keeps both rings and drops the tokens.
        bool tokens = w + .01f >= PreferredWidth;
        using var center = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        for (int i = 0; i < services.Length; i++)
        {
            var s = services[i];
            float start = tokens ? Pad + i * (RingCell + TokenGap + TokenWidth + ServiceGap) : Pad + i * (RingCell + RingGap);
            float cx = start + RingCell / 2f;
            float outerR = (diameter - outerStroke) / 2, innerR = outerR - outerStroke / 2 - ringGap - innerStroke / 2;
            DrawRing(g, cx, cy, outerR, outerStroke, s.FiveHour, s, stale, light, 255);
            DrawRing(g, cx, cy, innerR, innerStroke, s.Weekly, s, stale, light, 200);
            bool low = !s.Warning && s.Lowest is Quota q && q.Remaining < LowThreshold;
            string text = s.Center;
            using var font = Font(NumberFont, text.Length >= 3 ? diameter * .27f : diameter * .34f);
            using var brush = new SolidBrush(s.Warning || low ? Low : s.Stale ? stale : ink);
            g.DrawString(text, font, brush, new RectangleF(cx - outerR, cy - outerR + .5f, outerR * 2, outerR * 2), center);
            if (tokens && !s.Warning && s.Lowest is Quota lowest)
                DrawToken(g, start + RingCell + TokenGap, cy, lowest, s, now, light, ink, stale);
        }
        return bitmap;
    }

    // Two short lines beside a ring: which window is lowest (5h or 주) in the service's tone, and time until its reset.
    private static void DrawToken(Graphics g, float x, float cy, Quota q, RingService s, DateTimeOffset now, bool light, Color ink, Color stale)
    {
        bool low = q.Remaining < LowThreshold;
        using var top = Font(TokenFont, 11);
        using var bottom = Font(TokenFont, 12);
        using var topBrush = new SolidBrush(s.Stale ? stale : low ? Low : TextAccent(s, light));
        using var bottomBrush = new SolidBrush(s.Stale ? stale : ink);
        using var left = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(WindowName(q), top, topBrush, new RectangleF(x, cy - 15, TokenWidth, 14), left);
        g.DrawString(ShortCountdown(q, now), bottom, bottomBrush, new RectangleF(x, cy + 1, TokenWidth, 15), left);
    }

    // The empty track is a faint tone of the service color, so an empty or missing window still reads as that service.
    private static void DrawRing(Graphics g, float cx, float cy, float r, float stroke, Quota? q, RingService s, Color stale, bool light, int arcAlpha)
    {
        var box = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        var tone = s.Stale ? stale : s.Accent;
        using (var track = new Pen(Color.FromArgb(light ? 64 : 58, tone), stroke)) g.DrawEllipse(track, box);
        if (q == null || s.Warning) return;
        float sweep = (float)(Math.Clamp(q.Remaining, 0, 100) / 100 * 360);
        var baseColor = s.Stale ? stale : q.Remaining < LowThreshold ? Low : s.Accent;
        using var arc = new Pen(Color.FromArgb(arcAlpha, baseColor), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (sweep >= 359.5f) g.DrawEllipse(arc, box);
        // Zero remaining still shows a dot at the top, like an unclosed activity ring.
        else g.DrawArc(arc, box, -90, Math.Max(sweep, .1f));
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>For previews and tests: the transparent artwork over a plain taskbar color.</summary>
    internal static Bitmap Composite(Bitmap artwork, bool light)
    {
        var result = new Bitmap(artwork.Width, artwork.Height);
        using var g = Graphics.FromImage(result);
        g.Clear(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
        g.DrawImage(artwork, 0, 0, artwork.Width, artwork.Height);
        return result;
    }
}
