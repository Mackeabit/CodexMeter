using System;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexMeter;

internal static class PositionVerification
{
    internal static async Task Run(MainWindow window, Action<bool, string> check)
    {
        var original = new Point(window.Left, window.Top);
        bool wasVisible = window.IsVisible;
        var preferences = typeof(MainWindow).GetField("prefs", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string Settings() => JsonSerializer.Serialize(preferences.GetValue(window));
        string originalSettings = Settings();
        Task Idle() => window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
        Rect WorkArea()
        {
            var area = Forms.Screen.FromHandle(new WindowInteropHelper(window).Handle).WorkingArea;
            var transform = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformFromDevice;
            return new Rect(transform.Transform(new Point(area.Left, area.Top)), transform.Transform(new Point(area.Right, area.Bottom)));
        }
        bool Same(Point a, Point b) => Math.Abs(a.X - b.X) < .01 && Math.Abs(a.Y - b.Y) < .01;
        try
        {
            var area = WorkArea();
            window.Left = area.Left + Math.Min(32, Math.Max(0, area.Width - window.ActualWidth));
            window.Top = area.Top + Math.Min(32, Math.Max(0, area.Height - window.ActualHeight));
            await Idle();
            var chosen = new Point(window.Left, window.Top);
            window.OnDisplayChanged(null, EventArgs.Empty);
            window.OnDisplayChanged(null, EventArgs.Empty);
            await Idle();
            check(Same(chosen, new Point(window.Left, window.Top)), "display changes preserve an already visible user-selected position");

            window.Hide();
            window.OnDisplayChanged(null, EventArgs.Empty);
            await Idle();
            check(!window.IsVisible && Same(chosen, new Point(window.Left, window.Top)), "display changes preserve a hidden window's position without showing it");

            var desktop = Forms.SystemInformation.VirtualScreen;
            var transform = PresentationSource.FromVisual(window)!.CompositionTarget!.TransformFromDevice;
            var outside = transform.Transform(new Point(desktop.Right + 10000, desktop.Bottom + 10000));
            window.Left = outside.X; window.Top = outside.Y;
            await Idle();
            window.OnDisplayChanged(null, EventArgs.Empty);
            await Idle();
            area = WorkArea();
            check(!window.IsVisible && window.Left >= area.Left - 1 && window.Top >= area.Top - 1 &&
                window.Left <= Math.Max(area.Left, area.Right - window.ActualWidth) + 1 &&
                window.Top <= Math.Max(area.Top, area.Bottom - window.ActualHeight) + 1,
                "off-screen hidden windows move back into the nearest working area without being shown");
            check(Settings() == originalSettings, "display position correction leaves view, taskbar, opacity, polling and alert preferences unchanged");
        }
        finally
        {
            window.Left = original.X; window.Top = original.Y;
            if (wasVisible) window.ShowWidget(); else window.Hide();
        }
    }
}
