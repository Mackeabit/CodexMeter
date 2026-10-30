using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace CodexMeter;

public static class TrayArtwork
{
    public static void SavePreview(string filename)
    {
        using var canvas = new Bitmap(500, 124);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(31,31,31));
        using var lightBackground = new SolidBrush(Color.FromArgb(246,246,246));
        g.FillRectangle(lightBackground, 0, 62, 500, 62);
        using var font = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        for (int row = 0; row < 2; row++)
        {
            using var label = new SolidBrush(row == 0 ? Color.White : Color.Black);
            int x = 20;
            foreach (var size in new[] { 16, 20, 24, 32 })
            {
                g.DrawString($"{size}px", font, label, x, row * 62 + 6);
                using var icon = Render("25", size, row == 1);
                g.DrawImageUnscaled(icon, x, row * 62 + 24);
                using var hundred = Render("100", size, row == 1);
                g.DrawImageUnscaled(hundred, x + 46, row * 62 + 24);
                x += 120;
            }
        }
        canvas.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
    }
    public static bool IsLightTaskbar()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return key?.GetValue("SystemUsesLightTheme") is int value && value == 1; }
        catch { return false; }
    }
    public static Bitmap Render(string text, int size, bool light, bool warning = false, string style = "number", double? remaining = null)
    {
        if (style == "battery") return RenderBattery(text, size, light, warning ? null : remaining);
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var family = new FontFamily("Arial");
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, 32, PointF.Empty, StringFormat.GenericTypographic);
        var box = path.GetBounds();
        if (box.Width <= 0 || box.Height <= 0) return bitmap;
        // Fit the actual ink, not the font's invisible line-box, into the icon.
        float scale = Math.Min((size - 1.5f) / box.Width, (size - 3f) / box.Height);
        using var transform = new Matrix(scale, 0, 0, scale, (size - box.Width * scale) / 2 - box.Left * scale, (size - box.Height * scale) / 2 - box.Top * scale);
        path.Transform(transform);
        using var edge = new Pen(light ? Color.FromArgb(160,255,255,255) : Color.FromArgb(160,0,0,0), Math.Max(.6f, size / 40f)) { LineJoin = LineJoin.Round };
        using var ink = new SolidBrush(light ? Color.FromArgb(18,18,18) : Color.White);
        g.DrawPath(edge, path); g.FillPath(ink, path);
        return bitmap;
    }
    private static Bitmap RenderBattery(string text, int size, bool light, double? remaining)
    {
        var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var color = light ? Color.FromArgb(18,18,18) : Color.White;
        var background = light ? Color.FromArgb(246,246,246) : Color.FromArgb(31,31,31);
        using var ink = new SolidBrush(color);
        using var paper = new SolidBrush(background);
        using var charge = new SolidBrush(light ? Color.FromArgb(190,190,190) : Color.FromArgb(86,86,86));
        float stroke = Math.Max(1, size / 24f);
        // The cell is square, but the battery silhouette should be horizontal.
        float terminalWidth = Math.Max(1.5f, size / 16f);
        float margin = stroke / 2;
        float bodyWidth = size - terminalWidth - stroke;
        float bodyHeight = bodyWidth / 1.65f;
        var body = new RectangleF(margin, (size - bodyHeight) / 2,
            bodyWidth, bodyHeight);
        float radius = size * .08f;
        using var shell = RoundedRectangle(body, radius);
        g.FillPath(paper, shell);
        using var outline = new Pen(color, stroke);
        g.DrawPath(outline, shell);
        float terminalHeight = bodyHeight * .4f;
        g.FillRectangle(ink, body.Right + stroke / 2, (size - terminalHeight) / 2, terminalWidth, terminalHeight);

        var interior = RectangleF.Inflate(body, -stroke, -stroke);
        using var inner = RoundedRectangle(interior, Math.Max(.5f, radius - stroke));
        float fraction = remaining is double value && double.IsFinite(value)
            ? (float)(Math.Clamp(value, 0, 100) / 100) : 0;
        var fill = new RectangleF(interior.X, interior.Y, interior.Width * fraction, interior.Height);
        var saved = g.Save();
        g.SetClip(inner);
        if (fraction > 0) g.FillRectangle(charge, fill);
        g.Restore(saved);

        using var family = new FontFamily("Arial");
        using var glyph = new GraphicsPath();
        glyph.AddString(text, family, (int)FontStyle.Bold, 32, PointF.Empty, StringFormat.GenericTypographic);
        var box = glyph.GetBounds();
        if (box.Width <= 0 || box.Height <= 0) return bitmap;
        // Size the foreground independently of the battery's shallow silhouette.
        // These are the large-number bounds from v1.5, not the battery interior.
        float numberWidth = size - Math.Max(1, size / 24f) - 3 * stroke - .5f;
        float numberHeight = size - 3 * stroke - 2;
        float scale = Math.Min(numberWidth / box.Width, numberHeight / box.Height);
        using var transform = new Matrix(scale, 0, 0, scale,
            body.X + (body.Width - box.Width * scale) / 2 - box.Left * scale,
            body.Y + (body.Height - box.Height * scale) / 2 - box.Top * scale);
        glyph.Transform(transform);
        // A fine knockout separates large digits from the outline behind them.
        using var separator = new Pen(background, Math.Max(.8f, size / 24f)) { LineJoin = LineJoin.Round };
        g.DrawPath(separator, glyph);
        g.FillPath(ink, glyph);
        return bitmap;
    }
    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
    public static void SaveStylePreview(string filename)
    {
        using var canvas = new Bitmap(600, 480);
        using var g = Graphics.FromImage(canvas);
        g.Clear(Color.FromArgb(31,31,31));
        using var lightBackground = new SolidBrush(Color.FromArgb(246,246,246));
        g.FillRectangle(lightBackground, 0, 240, 600, 240);
        using var font = new Font("Segoe UI", 11, FontStyle.Regular, GraphicsUnit.Pixel);
        for (int row = 0; row < 2; row++)
        {
            using var label = new SolidBrush(row == 0 ? Color.White : Color.Black);
            g.DrawString("Size       Number        Battery + number:  0 / 23 / 50 / 100 / ! / unknown", font, label, 16, row * 240 + 10);
            int sizeRow = 0;
            foreach (int size in new[] { 16, 20, 24, 32 })
            {
                int y = row * 240 + 40 + sizeRow * 48;
                g.DrawString($"{size}px", font, label, 16, y + 4);
                using var number = Render("23", size, row == 1);
                g.DrawImageUnscaled(number, 90, y);
                int column = 0;
                foreach (var value in new[] { 0, 23, 50, 100, -1, -2 })
                {
                    string text = value == -2 ? "—" : value == -1 ? "!" : value.ToString();
                    using var icon = Render(text, size, row == 1, value == -1, "battery", value < 0 ? null : value);
                    g.DrawImageUnscaled(icon, 170 + column * 65, y);
                    column++;
                }
                sizeRow++;
            }
        }
        canvas.Save(filename, System.Drawing.Imaging.ImageFormat.Png);
    }
}
