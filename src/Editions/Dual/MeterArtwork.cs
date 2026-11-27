using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;

namespace CodexMeter;

internal static class MeterArtwork
{
    internal const int PreferredWidth = 336;
    internal const int MinimumWidth = 240;
    internal static (string Date, string Time) ResetParts(Quota? quota) => quota?.Reset is DateTimeOffset reset
        ? (reset.ToLocalTime().ToString("MM/dd", CultureInfo.InvariantCulture), reset.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))
        : ("리셋 시간", "미제공");

    internal static Bitmap Render(int width, int height, float scale, Quota?[] rows, bool stale, bool denied, bool light)
    {
        if (width + .01f < MinimumWidth * scale) throw new ArgumentOutOfRangeException(nameof(width), "Reset information needs its reserved width.");
        var bitmap = new Bitmap(Math.Max(1, width), Math.Max(1, height));
        using var g = Graphics.FromImage(bitmap);
        g.Clear(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
        g.ScaleTransform(scale, scale);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float w = width / scale, h = height / scale, rowHeight = h / rows.Length;
        bool single = rows.Length == 1;
        using var empty = new SolidBrush(light ? Color.FromArgb(211, 211, 211) : Color.FromArgb(66, 66, 66));
        using var ink = new SolidBrush(light ? Color.FromArgb(32, 32, 32) : Color.FromArgb(240, 240, 240));
        using var muted = new SolidBrush(light ? Color.FromArgb(89, 89, 89) : Color.FromArgb(188, 188, 188));
        using var rule = new Pen(light ? Color.FromArgb(199, 199, 199) : Color.FromArgb(81, 81, 81));
        using var dateFont = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var timeFont = new Font("Segoe UI", single ? 19 : 14, FontStyle.Regular, GraphicsUnit.Pixel);
        using var resetFont = new Font("Malgun Gothic", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        using var numberFont = new Font("Segoe UI", single ? Math.Min(30, h - 8) : Math.Min(17, rowHeight - 3), FontStyle.Regular, GraphicsUnit.Pixel);
        using var percentFont = new Font("Segoe UI", single ? 17 : 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var fmt = new StringFormat(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
        float ruleX = w - 110, textX = ruleX + 13;
        g.DrawLine(rule, ruleX, 3, ruleX, h - 3);
        for (int row = 0; row < rows.Length; row++)
        {
            var q = rows[row];
            bool warning = stale || denied || q?.ResetPending(DateTimeOffset.Now) == true;
            float top = row * rowHeight;
            string digits = warning ? "!" : q == null ? "—" : Math.Floor(q.Remaining).ToString("0", CultureInfo.InvariantCulture);
            float valueRight = ruleX - 12;
            float valueWidth = single ? (digits.Length > 2 ? 68 : 57) : 43;
            float trackWidth = Math.Max(12, valueRight - valueWidth - 12);
            float cellHeight = single ? Math.Min(24, h - 8) : 14;
            float cellY = top + (rowHeight - cellHeight) / 2;
            float gap = single ? (trackWidth < 70 ? 1 : 3) : 2;
            float cellWidth = (trackWidth - 9 * gap) / 10;
            using var fill = new SolidBrush(warning ? Color.Gray : Color.FromArgb(222, 128, 94));
            for (int i = 0; i < 10; i++)
            {
                var cell = new RectangleF(2 + i * (cellWidth + gap), cellY, cellWidth, cellHeight);
                g.FillRoundedRectangle(empty, Rectangle.Round(cell), new Size(single ? 3 : 2, single ? 3 : 2));
                float fraction = q == null ? 0 : (float)Math.Clamp(q.Remaining / 10 - i, 0, 1);
                var saved = g.Save();
                g.SetClip(new RectangleF(cell.X, cell.Y, cell.Width * fraction, cell.Height));
                g.FillRoundedRectangle(fill, Rectangle.Round(cell), new Size(single ? 3 : 2, single ? 3 : 2));
                g.Restore(saved);
            }
            bool showPercent = !warning && q != null;
            float numberWidth = g.MeasureString(digits, numberFont, PointF.Empty, fmt).Width;
            float suffixWidth = showPercent ? g.MeasureString("%", percentFont, PointF.Empty, fmt).Width : 0;
            float numberX = valueRight - numberWidth - suffixWidth - (showPercent ? 1 : 0);
            g.DrawString(digits, numberFont, ink, new RectangleF(numberX, top, numberWidth + 1, rowHeight), fmt);
            if (showPercent) g.DrawString("%", percentFont, muted, new RectangleF(valueRight - suffixWidth, top + (single ? 4 : 1), suffixWidth + 1, rowHeight), fmt);
            var reset = ResetParts(q);
            if (single)
            {
                float dateY = (h - 38) / 2;
                g.DrawString(reset.Date, q?.Reset == null ? resetFont : dateFont, muted, new RectangleF(textX, dateY, 95, 16), fmt);
                if (q?.Reset != null) g.DrawString("리셋", resetFont, muted, new RectangleF(textX + 40, dateY, 45, 16), fmt);
                g.DrawString(reset.Time, q?.Reset == null ? resetFont : timeFont, ink, new RectangleF(textX, dateY + 17, 95, 21), fmt);
            }
            else if (q?.Reset == null)
                g.DrawString("시간 미제공", resetFont, muted, new RectangleF(textX, top, 95, rowHeight), fmt);
            else
            {
                float dateWidth = g.MeasureString(reset.Date, dateFont, PointF.Empty, fmt).Width;
                g.DrawString(reset.Date, dateFont, muted, new RectangleF(textX, top, dateWidth + 1, rowHeight), fmt);
                g.DrawString(reset.Time, timeFont, ink, new RectangleF(textX + dateWidth + 6, top, 50, rowHeight), fmt);
            }
        }
        return bitmap;
    }
}
