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
    public static Bitmap Render(string text, int size, bool light)
    {
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
}
