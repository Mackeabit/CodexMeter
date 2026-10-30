using System.Drawing;
using System.Drawing.Drawing2D;

namespace CodexMeter;

internal static class DrawingCompatibility
{
    // .NET 8 counterpart of Graphics.FillRoundedRectangle used by the existing artwork.
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF rect, SizeF radius)
    {
        float width = System.Math.Min(rect.Width, radius.Width * 2), height = System.Math.Min(rect.Height, radius.Height * 2);
        if (width <= 0 || height <= 0) { graphics.FillRectangle(brush, rect); return; }
        using var path = new GraphicsPath();
        path.AddArc(rect.Left, rect.Top, width, height, 180, 90);
        path.AddArc(rect.Right - width, rect.Top, width, height, 270, 90);
        path.AddArc(rect.Right - width, rect.Bottom - height, width, height, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - height, width, height, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
