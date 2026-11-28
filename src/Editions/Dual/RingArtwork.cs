using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;

namespace CodexMeter;

/// <summary>
/// One service on the taskbar: outer ring = weekly window, inner ring = 5-hour window.
/// <paramref name="NoFiveHour"/> means Codex's response for this account carried no 5-hour window at all,
/// which is different from a 5-hour value that is merely unknown. <paramref name="At"/> is the render time.
/// </summary>
internal sealed record RingService(string Short, Color Accent, Quota? FiveHour, Quota? Weekly, bool Stale, bool Warning, bool NoFiveHour = false, DateTimeOffset At = default)
{
    public static readonly Color CodexAccent = Color.FromArgb(222, 128, 94);
    public static readonly Color ClaudeAccent = Color.FromArgb(143, 134, 230);
    /// <summary>The center number: the 5-hour window while it has anything left, otherwise the weekly window.</summary>
    public Quota? Shown => FiveHour is { Remaining: > 0 } ? FiveHour : Weekly ?? FiveHour;
    /// <summary>
    /// A window at 0% blocks use until it resets; with both exhausted, use resumes at the later reset.
    /// Once its reset time has passed the old 0% no longer proves anything, so it stops blocking.
    /// </summary>
    public Quota? Blocking => Warning ? null : new[] { FiveHour, Weekly }.Where(q => q is { Remaining: <= 0 } && !q.ResetPending(At))
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
    // Full layout (the normal one): beside each ring, a 5시간 row and a 주간 row with remaining percent and the time
    // left until that reset ("6일 13시간", "4시간 17분"); the row behind the center number gets a service-colored dot
    // and tint, like the detail window. Words are never abbreviated here.
    internal const int RowGap = 5;
    internal const int RowWidth = 160;   // time column 82 DIP: the longest time measured 74 here, 76.3 on the CI runner
    internal const int WideServiceGap = 9;
    internal const int WideWidth = Pad + 2 * (RingCell + RowGap + RowWidth) + WideServiceGap + Pad;
    // Two 16 DIP rows and the 2 DIP between them need 34 DIP. A 40 px taskbar at 100% leaves 36 DIP for the meter
    // (observed on the user's desktop), which is enough; only a smaller taskbar keeps the short token.
    internal const int WideMinimumHeight = 2 * 16 + 2;
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

    internal static string WindowName(Quota q) => q.Duration;   // 5시간, 주간: never abbreviated

    /// <summary>Time left until a reset in words: 6일 13시간, 4시간 17분, 42분; 확인 중 once it has passed.</summary>
    internal static string LongCountdown(Quota? q, DateTimeOffset now)
    {
        if (q?.Reset is not DateTimeOffset reset) return "—";
        var span = reset - now;
        if (span.TotalSeconds <= 0) return "확인 중";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}일 {span.Hours}시간";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}시간 {span.Minutes}분";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}분";
    }

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
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
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
        // 3 DIP above and below the ring. A 40 px taskbar at 100% (36 DIP meter) gets a 30 DIP ring instead of 28, which
        // leaves its center number a clear gap to the inner ring; the 150% laptop taskbar stays at the 36 DIP cap.
        float diameter = DesktopDiameter(h);
        float outerStroke = Math.Max(3, diameter * .1f), innerStroke = Math.Max(2.5f, diameter * .085f), ringGap = Math.Max(2f, diameter * .065f);
        float cy = h / 2;
        // Full rows beside each ring; only when no space anywhere fits them, the short token; then rings only.
        // One device pixel of slack: a tier width times a fractional scale may be rounded either way.
        float slack = 1f / scale + .01f;
        bool tall = h + slack >= WideMinimumHeight;
        bool rows = tall && w + slack >= WideWidth;
        bool tokens = !rows && w + slack >= PreferredWidth;
        for (int i = 0; i < services.Length; i++)
        {
            var s = services[i];
            float start = rows ? Pad + i * (RingCell + RowGap + RowWidth + WideServiceGap)
                : tokens ? Pad + i * (RingCell + TokenGap + TokenWidth + ServiceGap) : Pad + i * (RingCell + RingGap);
            float cx = start + RingCell / 2f;
            float outerR = (diameter - outerStroke) / 2, innerR = outerR - outerStroke / 2 - ringGap - innerStroke / 2;
            DrawRings(g, cx, cy, outerR, outerStroke, innerR, innerStroke, s, stale, light);
            string text = s.Center;
            TextArt.DrawCentered(g, scale, text, CenterFontSize(text, diameter), s.Warning || s.Alert ? Low : s.Stale ? stale : ink, cx, cy, InnerOpening(diameter));
            if (rows) DrawRows(g, scale, start + RingCell + RowGap, cy, s, now, light, ink, stale);
            else if (tokens && !s.Warning && s.Shown is Quota shown)
                DrawToken(g, scale, start + RingCell + TokenGap, cy, shown, s, now, light, ink, stale);
        }
        return bitmap;
    }

    /// <summary>Taskbar ring diameter for a meter <paramref name="heightDip"/> tall: 3 DIP margin, at most 36.</summary>
    internal static float DesktopDiameter(float heightDip) => Math.Min(RingCell - 4, heightDip - 6);

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
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var ink = light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(242, 242, 242);
        var stale = light ? Color.FromArgb(150, 150, 150) : Color.FromArgb(128, 128, 128);
        float diameter = sizeDip - 2, c = sizeDip / 2;
        float outerStroke = diameter * .085f, innerStroke = diameter * .075f, ringGap = diameter * .055f;
        float outerR = (diameter - outerStroke) / 2, innerR = outerR - outerStroke / 2 - ringGap - innerStroke / 2;
        DrawRings(g, c, c, outerR, outerStroke, innerR, innerStroke, s, stale, light);
        string text = s.Center;
        TextArt.DrawCentered(g, scale, text, CenterFontSize(text, diameter * .8f), s.Warning || s.Alert ? Low : s.Stale ? stale : ink, c, c, 2 * (innerR - innerStroke / 2));
        return bitmap;
    }

    /// <summary>
    /// Ring number size: two digits fill most of the space inside the inner ring; three digits ("100") step down so
    /// they still clear it. Taskbar rings pass their diameter; the detail window's thinner rings pass 80% of theirs.
    /// </summary>
    internal static float CenterFontSize(string text, float diameter) =>
        // Small rings keep minimum stroke widths, so their opening shrinks faster than the ring: cap by the opening
        // (two digits are about 1.12 em wide in Segoe UI Variable Semibold, three about 1.6 em).
        Math.Min(diameter * (text.Length >= 3 ? .31f : .42f), InnerOpening(diameter) / (text.Length >= 3 ? 1.65f : 1.15f));

    /// <summary>Clear width inside the taskbar ring's inner stroke (same geometry as <see cref="Render"/>).</summary>
    internal static float InnerOpening(float diameter)
    {
        float outerStroke = Math.Max(3, diameter * .1f), innerStroke = Math.Max(2.5f, diameter * .085f), ringGap = Math.Max(2f, diameter * .065f);
        float outerR = (diameter - outerStroke) / 2, innerR = outerR - outerStroke / 2 - ringGap - innerStroke / 2;
        return 2 * (innerR - innerStroke / 2);
    }

    internal const string BlockedToken = "불가";

    // Two short lines beside a ring (fallback when the full rows fit nowhere): which window the center number is
    // (5시간 or 주간) and the time until its reset.
    // A blocked service says 불가 and counts down to when it can be used again.
    internal static (string Top, string Bottom) Token(RingService s, Quota shown, DateTimeOffset now) => s.Blocking is Quota blocking
        ? (BlockedToken, ShortCountdown(blocking, now))
        : (WindowName(shown), ShortCountdown(shown, now));

    private static void DrawToken(Graphics g, float scale, float x, float cy, Quota q, RingService s, DateTimeOffset now, bool light, Color ink, Color stale)
    {
        var (topText, bottomText) = Token(s, q, now);
        TextArt.Draw(g, scale, topText, TextArt.Face.Text, 11, s.Stale ? stale : s.Alert ? Low : TextAccent(s, light), new RectangleF(x, cy - 15, TokenWidth, 14));
        TextArt.Draw(g, scale, bottomText, TextArt.Face.Text, 12, s.Stale ? stale : ink, new RectangleF(x, cy + 1, TokenWidth, 15));
    }

    internal const int RowHeight = 16;
    // Row columns inside RowWidth: [dot][label 5시간/주간][value right-aligned][time left].
    internal const float RowLabelX = 8, RowLabelWidth = 32, RowValueRight = 72, RowTimeX = 77, RowTimeWidth = RowWidth - RowTimeX - 1;
    internal const float RowTextSize = 12, RowValueSize = RowTextSize + .5f;
    // Text tones on the taskbar: values in full ink, times nearly as strong, labels one step softer. The earlier single
    // muted grey (176 on dark, 92 on light) read as disabled; these keep a clear hierarchy without looking greyed out.
    internal static Color LabelTone(bool light) => light ? Color.FromArgb(64, 64, 64) : Color.FromArgb(210, 210, 210);
    internal static Color TimeTone(bool light) => light ? Color.FromArgb(40, 40, 40) : Color.FromArgb(228, 228, 228);

    internal static (string Label, string Value, string Time) RowText(RingService s, Quota? q, bool fiveHour, DateTimeOffset now)
    {
        string label = fiveHour ? "5시간" : "주간";
        if (fiveHour && s.NoFiveHour && q == null) return (label, "없음", "주간 한도만");
        if (q == null) return (label, "—", "");
        bool exhausted = q.Remaining <= 0 && !q.ResetPending(now);
        string time = LongCountdown(q, now);
        return (label, Math.Floor(q.Remaining).ToString("0", CultureInfo.InvariantCulture) + "%", exhausted ? time + " 후" : time);
    }

    // Two rows (5h on top, 주 below) beside a ring. The shown row gets the service dot and a faint tint of its color;
    // an exhausted row is red. The taskbar version of the detail window's rows.
    private static void DrawRows(Graphics g, float scale, float x, float cy, RingService s, DateTimeOffset now, bool light, Color ink, Color stale)
    {
        var muted = LabelTone(light);
        foreach (var (q, fiveHour, top) in new[] { (s.FiveHour, true, cy - RowHeight - 1f), (s.Weekly, false, cy + 1f) })
        {
            var (label, value, time) = RowText(s, q, fiveHour, now);
            bool shown = !s.Warning && s.IsShown(q);
            bool exhausted = !s.Warning && q is { Remaining: <= 0 } && !q.ResetPending(s.At);
            bool low = !s.Warning && q != null && q.Remaining < LowThreshold;
            var row = new RectangleF(x, top, RowWidth, RowHeight);
            if (shown)
            {
                using var tint = new SolidBrush(Color.FromArgb(light ? 44 : 62, s.Stale ? stale : s.Accent));
                using var path = RoundedRect(row, 3);
                g.FillPath(tint, path);
                using var dot = new SolidBrush(s.Stale ? stale : s.Accent);
                g.FillEllipse(dot, x + 2.5f, top + RowHeight / 2f - 2f, 3.6f, 3.6f);
            }
            var valueColor = s.Stale ? stale : s.Warning ? muted : low || exhausted ? Low : value == "없음" ? muted : ink;
            bool word = value == "없음";
            TextArt.Draw(g, scale, label, TextArt.Face.Text, RowTextSize, s.Stale ? stale : muted, new RectangleF(x + RowLabelX, top, RowLabelWidth, RowHeight));
            TextArt.Draw(g, scale, value, word ? TextArt.Face.Text : TextArt.Face.Number, word ? RowTextSize : RowValueSize, valueColor,
                new RectangleF(x + RowLabelX + RowLabelWidth, top, RowValueRight - RowLabelX - RowLabelWidth, RowHeight), StringAlignment.Far);
            TextArt.Draw(g, scale, time, TextArt.Face.Text, RowTextSize, s.Stale ? stale : exhausted ? Low : TimeTone(light), new RectangleF(x + RowTimeX, top, RowTimeWidth, RowHeight));
        }
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
