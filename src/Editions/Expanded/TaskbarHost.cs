using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Forms = System.Windows.Forms;

namespace CodexMeter;

internal static class TaskbarNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public Rectangle Bounds => Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    internal delegate bool EnumProc(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hwnd, int show);
    [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr hwnd, EnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder name, int capacity);
    internal static string ClassName(IntPtr hwnd) { var b = new StringBuilder(256); GetClassName(hwnd, b, b.Capacity); return b.ToString(); }
    internal static string Title(IntPtr hwnd) { var b = new StringBuilder(128); GetWindowText(hwnd, b, b.Capacity); return b.ToString(); }
    internal static void CaptureTaskbar(string path)
    {
        var bar = FindWindow("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero || !GetWindowRect(bar, out var rect) || rect.Bounds.Width <= 0 || rect.Bounds.Height <= 0 || (long)rect.Bounds.Width * rect.Bounds.Height > 4000000) throw new InvalidOperationException("Taskbar capture unavailable");
        using var bitmap = new Bitmap(rect.Bounds.Width, rect.Bounds.Height);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    internal static object Inspect()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        GetWindowRect(taskbar, out var r);
        var children = new List<object>();
        EnumChildWindows(taskbar, (h, _) => { GetWindowRect(h, out var c); bool meter = Title(h) == "Codex Meter Taskbar"; var hit = meter ? WindowFromPoint(new Point(c.Left + c.Bounds.Width / 2, c.Top + c.Bounds.Height / 2)) : IntPtr.Zero; children.Add(new { handle = h.ToInt64(), parent = GetParent(h).ToInt64(), cls = ClassName(h), isMeter = meter, visible = IsWindowVisible(h), bounds = c.Bounds, hit = hit.ToInt64(), hitClass = hit == IntPtr.Zero ? null : ClassName(hit) }); return true; }, IntPtr.Zero);
        return new { taskbar = taskbar.ToInt64(), dpi = GetDpiForWindow(taskbar), bounds = r.Bounds, children, occupied = ReadOccupied(taskbar) };
    }
    internal static Rectangle[] ReadOccupied(IntPtr taskbar)
    {
        var root = AutomationElement.FromHandle(taskbar);
        var condition = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton));
        var items = root.FindAll(TreeScope.Descendants, condition);
        var rects = new List<Rectangle>();
        foreach (AutomationElement item in items)
        {
            var c = item.Current;
            if (c.IsOffscreen || c.BoundingRectangle.IsEmpty) continue;
            var r = c.BoundingRectangle;
            if (r.Width > 0 && r.Height > 0) rects.Add(Rectangle.FromLTRB((int)Math.Floor(r.Left), (int)Math.Floor(r.Top), (int)Math.Ceiling(r.Right), (int)Math.Ceiling(r.Bottom)));
        }
        return rects.ToArray();
    }
}

/// <summary>Owns only our child window. Never resizes or subclasses Explorer windows.</summary>
internal sealed class TaskbarHost : Forms.NativeWindow, IDisposable
{
    private IntPtr parent;
    private Snapshot? snapshot;
    private bool stale;
    private float scale = 1;
    private System.Threading.Tasks.Task<Rectangle[]>? occupancy;
    private Rectangle[]? occupied;
    private DateTime nextScan;
    private DateTime scanTime;
    private readonly Action showDetail;
    private readonly Forms.ContextMenuStrip menu = new();
    private UsageToolTip? hover;
    public string Status { get; private set; } = "연결 대기 중";
    public bool Attached => Handle != IntPtr.Zero && TaskbarNative.GetParent(Handle) == parent && TaskbarNative.IsWindowVisible(Handle);
    public void ReportFailure() { hover?.Hide(); Status = "연결 재시도 중"; if (Handle != IntPtr.Zero) TaskbarNative.ShowWindow(Handle, 0); }
    public TaskbarHost(Action showDetail, Action disable, Action refresh)
    {
        this.showDetail = showDetail;
        menu.Items.Add("상세 보기", null, (_, _) => showDetail());
        menu.Items.Add("새로고침", null, (_, _) => refresh());
        menu.Items.Add("작업표시줄 표시 끄기", null, (_, _) => disable());
    }
    public void Update(Snapshot? value, bool old)
    {
        snapshot = value; stale = old;
        var bar = TaskbarNative.FindWindow("Shell_TrayWnd", null);
        if (bar == IntPtr.Zero) { hover?.Hide(); Status = "다시 연결 대기 중"; return; }
        if (bar != parent || Handle == IntPtr.Zero || !TaskbarNative.IsWindow(Handle))
        {
            hover?.Dispose(); hover = null;
            if (Handle != IntPtr.Zero) DestroyHandle();
            parent = bar; occupied = null; occupancy = null; nextScan = DateTime.MinValue;
            var previous = TaskbarNative.SetThreadDpiAwarenessContext(TaskbarNative.GetWindowDpiAwarenessContext(bar));
            try { CreateHandle(new Forms.CreateParams { Caption = "Codex Meter Taskbar", Parent = bar, Style = 0x40000000, ExStyle = 0x08080080, Width = 1, Height = 1 }); }
            finally { TaskbarNative.SetThreadDpiAwarenessContext(previous); }
        }
        scale = Math.Max(1, TaskbarNative.GetDpiForWindow(bar) / 96f);
        hover ??= new UsageToolTip(Handle);
        hover.Update(UsageHover.Text(snapshot, stale, DateTimeOffset.Now));
        if (occupancy?.IsCompleted == true)
        {
            if (occupancy.IsCompletedSuccessfully) { occupied = occupancy.Result.Length > 0 ? occupancy.Result : null; scanTime = DateTime.UtcNow; }
            else occupied = null;
            occupancy = null;
        }
        if (occupancy == null && DateTime.UtcNow >= nextScan)
        {
            nextScan = DateTime.UtcNow.AddSeconds(2);
            occupancy = System.Threading.Tasks.Task.Run(() => TaskbarNative.ReadOccupied(bar));
        }
        TaskbarNative.GetWindowRect(bar, out var screen);
        TaskbarNative.GetClientRect(bar, out var client);
        var notify = TaskbarNative.FindWindowEx(bar, IntPtr.Zero, "TrayNotifyWnd", null);
        TaskbarNative.GetWindowRect(notify, out var notification);
        int width = (int)Math.Round(MeterArtwork.PreferredWidth * scale);
        int height = (int)Math.Round(Math.Min(44 * scale, Math.Max((Rows().Length > 1 ? 32 : 24) * scale, client.Bounds.Height - 4 * scale)));
        var obstacles = occupied?.Where(r => r.IntersectsWith(screen.Bounds)).Select(r => new Rectangle(r.X - screen.Left, r.Y - screen.Top, r.Width, r.Height)).ToArray();
        int right = notify == IntPtr.Zero ? 0 : notification.Left - screen.Left - (int)(6 * scale);
        var position = FindMeterSpace(client.Bounds, obstacles, right, scale, height);
        if (position == null || DateTime.UtcNow - scanTime > TimeSpan.FromSeconds(10))
        {
            hover.Hide();
            TaskbarNative.ShowWindow(Handle, 0);
            Status = occupied == null ? "공간 확인 중" : "표시 안 됨 · 공간 부족";
            return;
        }
        var p = position.Value;
        TaskbarNative.SetWindowPos(Handle, IntPtr.Zero, p.X, p.Y, p.Width, p.Height, 0x0010 | 0x0040);
        Present(p.Width, p.Height);
        Status = p.Width < width ? "표시 중 · 막대 폭 자동 조절" : "표시 중";
    }
    internal static Rectangle? FindMeterSpace(Rectangle area, Rectangle[]? obstacles, int right, float scale, int height) =>
        FindResponsiveSpace(area, obstacles, right, (int)Math.Round(MeterArtwork.PreferredWidth * scale), (int)Math.Ceiling(MeterArtwork.MinimumWidth * scale), height, (int)(6 * scale));
    internal static Rectangle? FindSpace(Rectangle area, Rectangle[]? obstacles, int right, int width, int height, int gap)
    {
        right = Math.Min(right, area.Right - gap);
        if (obstacles == null || area.Height < height || right <= width) return null;
        int y = area.Top + (area.Height - height) / 2;
        for (int x = right - width; x >= gap; )
        {
            var candidate = new Rectangle(x, y, width, height);
            var overlaps = obstacles.Where(r => Rectangle.Inflate(r, gap, 0).IntersectsWith(candidate)).ToArray();
            if (overlaps.Length == 0) return candidate;
            x = overlaps.Min(r => r.Left) - gap - width;
        }
        return null;
    }
    internal static Rectangle? FindResponsiveSpace(Rectangle area, Rectangle[]? obstacles, int right, int preferredWidth, int minimumWidth, int height, int gap)
    {
        for (int width = preferredWidth; width >= minimumWidth; width--)
        {
            var space = FindSpace(area, obstacles, right, width, height, gap);
            if (space.HasValue) return space;
        }
        return null;
    }
    internal static void SavePreview(string path, Snapshot value, bool warning, float dpiScale, int width = MeterArtwork.PreferredWidth, bool? light = null)
    {
        using var host = new TaskbarHost(() => { }, () => { }, () => { });
        host.snapshot = value; host.stale = warning; host.scale = dpiScale;
        using var bitmap = MeterArtwork.Render((int)(width * dpiScale), (int)(44 * dpiScale), dpiScale, host.Rows(), warning, value.Allowed == false, light ?? TrayArtwork.IsLightTaskbar());
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
    private Quota?[] Rows()
    {
        var rows = snapshot?.Windows.Where(q => q.Bucket == "codex").OrderBy(q => q.Minutes).Take(2).Cast<Quota?>().ToArray();
        if (rows == null || rows.Length == 0) rows = snapshot?.Main is Quota q ? new Quota?[] { q } : new Quota?[] { null };
        return rows;
    }
    private Bitmap Render(int width, int height) =>
        MeterArtwork.Render(width, height, scale, Rows(), stale, snapshot?.Allowed == false, TrayArtwork.IsLightTaskbar());
    [StructLayout(LayoutKind.Sequential)] private struct Paint { public IntPtr DC; public int Erase; public TaskbarNative.Rect Rect; public int Restore, IncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst=32)] public byte[] Reserved; }
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out Paint paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref Paint paint);
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, IntPtr dstPoint, ref Size size, IntPtr srcDc, ref Point srcPoint, uint key, ref Blend blend, uint flags);
    private void Present(int width, int height)
    {
        // A normal GDI child surface is hidden by the Windows 11 composition island.
        // Submit an explicit layered surface to the compositor instead.
        using var bitmap = Render(width, height);
        var memory = CreateCompatibleDC(IntPtr.Zero);
        if (memory == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        var dib = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memory, dib);
        try
        {
            var size = new Size(width, height); var origin = Point.Empty;
            var blend = new Blend { Alpha = 255, Format = 1 };
            if (!UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, 2)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { SelectObject(memory, old); DeleteObject(dib); DeleteDC(memory); }
    }
    protected override void WndProc(ref Forms.Message m)
    {
        if (m.Msg == 0x000F)
        {
            var dc = BeginPaint(Handle, out var paint);
            EndPaint(Handle, ref paint);
            m.Result = IntPtr.Zero; return;
        }
        if (m.Msg == 0x0014) { m.Result = new IntPtr(1); return; }
        if (m.Msg == 0x0021) { m.Result = new IntPtr(3); return; }
        if (m.Msg == 0x0202) { hover?.Hide(); showDetail(); return; }
        if (m.Msg == 0x0205) { hover?.Hide(); menu.Show(Forms.Cursor.Position); return; }
        base.WndProc(ref m);
    }
    public void Dispose() { hover?.Dispose(); hover = null; if (Handle != IntPtr.Zero) DestroyHandle(); menu.Dispose(); }
}
