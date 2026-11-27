using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;

namespace CodexMeter;

/// <summary>
/// One service on the taskbar: outer ring = weekly window, inner ring = 5-hour window.
/// <paramref name="NoFiveHour"/> means a successful reading had no 5-hour window at all (a plan without that limit),
/// which is different from a 5-hour value that is merely unknown.
/// </summary>
internal sealed record RingService(string Short, Color Accent, Quota? FiveHour, Quota? Weekly, bool Stale, bool Warning, bool NoFiveHour = false)
{
    public static readonly Color CodexAccent = Color.FromArgb(222, 128, 94);
    public static readonly Color ClaudeAccent = Color.FromArgb(143, 134, 230);
    /// <summary>The center number: the 5-hour window while it has anything left, otherwise the weekly window.</summary>
    public Quota? Shown => FiveHour is { Remaining: > 0 } ? FiveHour : Weekly ?? FiveHour;
    /// <summary>A window at 0% blocks use until it resets; with both exhausted, use resumes at the later reset.</summary>
    public Quota? Blocking => Warning ? null : new[] { FiveHour, Weekly }.Where(q => q is { Remaining: <= 0 })
        .OrderByDescending(q => q!.Reset ?? DateTimeOffset.MaxValue).FirstOrDefault();
    public bool Blocked => Blocking != null;
    /// <summary>Red center: blocked, or the shown window under the low threshold.</summary>
    public bool Alert => !Warning && (Blocked || Shown is Quota q && q.Remaining < RingArtwork.LowThreshold);
    public string Center => Warning ? "!" : Shown is Quota q ? Math.Floor(q.Remaining).ToString("0", CultureInfo.InvariantCulture) : "—";
    public bool IsShown(Quota? q) => q != null && ReferenceEquals(q, Shown);
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
            DrawRings(g, cx, cy, outerR, outerStroke, innerR, innerStroke, s, stale, light);
            string text = s.Center;
            using var font = Font(NumberFont, text.Length >= 3 ? diameter * .27f : diameter * .34f);
            using var brush = new SolidBrush(s.Warning || s.Alert ? Low : s.Stale ? stale : ink);
            g.DrawString(text, font, brush, new RectangleF(cx - outerR, cy - outerR + .5f, outerR * 2, outerR * 2), center);
            if (tokens && !s.Warning && s.Shown is Quota shown)
                DrawToken(g, start + RingCell + TokenGap, cy, shown, s, now, light, ink, stale);
        }
        return bitmap;
    }

    /// <summary>One service's ring for the detail window: same geometry and colors as the taskbar, larger.</summary>
    internal static Bitmap RenderRing(RingService s, float sizeDip, float scale, bool light)
    {
        int px = Math.Max(1, (int)Math.Ceiling(sizeDip * scale));
        var bitmap = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        var ink = light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(242, 242, 242);
        var stale = light ? Color.FromArgb(150, 150, 150) : Color.FromArgb(128, 128, 128);
        float diameter = sizeDip - 2, c = sizeDip / 2;
        float outerStroke = diameter * .085f, innerStroke = diameter * .075f, ringGap = diameter * .055f;
        float outerR = (diameter - outerStroke) / 2, innerR = outerR - outerStroke / 2 - ringGap - innerStroke / 2;
        DrawRings(g, c, c, outerR, outerStroke, innerR, innerStroke, s, stale, light);
        string text = s.Center;
        using var font = Font(NumberFont, text.Length >= 3 ? diameter * .22f : diameter * .27f);
        using var brush = new SolidBrush(s.Warning || s.Alert ? Low : s.Stale ? stale : ink);
        using var center = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(text, font, brush, new RectangleF(0, .5f, sizeDip, sizeDip), center);
        return bitmap;
    }

    internal const string BlockedToken = "불가";

    // Two short lines beside a ring: which window the center number is (5h or 주) and time until its reset.
    // A blocked service says 불가 and counts down to when it can be used again.
    internal static (string Top, string Bottom) Token(RingService s, Quota shown, DateTimeOffset now) => s.Blocking is Quota blocking
        ? (BlockedToken, ShortCountdown(blocking, now))
        : (WindowName(shown), ShortCountdown(shown, now));

    private static void DrawToken(Graphics g, float x, float cy, Quota q, RingService s, DateTimeOffset now, bool light, Color ink, Color stale)
    {
        var (topText, bottomText) = Token(s, q, now);
        using var top = Font(TokenFont, 11);
        using var bottom = Font(TokenFont, 12);
        using var topBrush = new SolidBrush(s.Stale ? stale : s.Alert ? Low : TextAccent(s, light));
        using var bottomBrush = new SolidBrush(s.Stale ? stale : ink);
        using var left = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        g.DrawString(topText, top, topBrush, new RectangleF(x, cy - 15, TokenWidth, 14), left);
        g.DrawString(bottomText, bottom, bottomBrush, new RectangleF(x, cy + 1, TokenWidth, 15), left);
    }

    // Outer = weekly, inner = 5-hour. A plan without a 5-hour limit draws no inner track at all,
    // so it reads as a single ring rather than an unknown (faint, empty) one.
    private static void DrawRings(Graphics g, float cx, float cy, float outerR, float outerStroke, float innerR, float innerStroke, RingService s, Color stale, bool light)
    {
        DrawRing(g, cx, cy, outerR, outerStroke, s.Weekly, s, stale, light, 255);
        if (!s.NoFiveHour) DrawRing(g, cx, cy, innerR, innerStroke, s.FiveHour, s, stale, light, 200);
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
