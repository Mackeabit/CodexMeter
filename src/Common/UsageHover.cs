using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodexMeter;

internal static class UsageHover
{
    internal static Quota?[] Rows(Snapshot? snapshot)
    {
        var rows = snapshot?.Windows.Where(q => q.Bucket == "codex").OrderBy(q => q.Minutes).Take(2).Cast<Quota?>().ToArray();
        return rows is { Length: > 0 } ? rows : new Quota?[] { snapshot?.Main };
    }

    internal static string Describe(Quota? quota, bool stale, bool denied, DateTimeOffset now)
    {
        if (quota == null) return "사용량 미확인 · 리셋 시간 미제공";
        string reset = quota.Reset?.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "시간 미제공";
        string status = denied ? "사용 제한 · " : stale || quota.ResetPending(now) ? "갱신 확인 필요 · 마지막 확인값 · " : "";
        return $"{status}{quota.Remaining:0.#}% 남음\n{reset} 리셋 · {quota.Countdown(now)}";
    }

    internal static string Text(Snapshot? snapshot, bool stale, DateTimeOffset now) =>
        "Codex Meter · 다음 리셋 (PC 현지시간)\n\n" +
        string.Join("\n\n", Rows(snapshot).Select(q => Describe(q, stale, snapshot?.Allowed == false, now)));
}

// A native tooltip subclasses only our own meter HWND. Explorer is never subclassed.
// The system handles hover delay, mouse leave, monitor placement and non-activation.
internal sealed class UsageToolTip : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct CommonControls { public int Size, Classes; }
    [StructLayout(LayoutKind.Sequential)] private struct ToolInfo
    {
        public uint Size, Flags;
        public IntPtr Window, Id;
        public TaskbarNative.Rect Rect;
        public IntPtr Instance, Text, Param, Reserved;
    }
    [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref CommonControls controls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, ref ToolInfo info);
    private IntPtr handle, text;
    private ToolInfo tool;
    private string content = "";
    internal IntPtr Handle => handle;
    internal bool Registered => handle != IntPtr.Zero && SendMessage(handle, 0x040D, IntPtr.Zero, IntPtr.Zero).ToInt32() == 1;
    internal string RegisteredText
    {
        get { var info = tool; return handle != IntPtr.Zero && SendMessage(handle, 0x0435, IntPtr.Zero, ref info) != IntPtr.Zero ? Marshal.PtrToStringUni(info.Text) ?? "" : ""; }
    }
    internal UsageToolTip(IntPtr owner)
    {
        var controls = new CommonControls { Size = Marshal.SizeOf<CommonControls>(), Classes = 0xFF };
        InitCommonControlsEx(ref controls);
        handle = CreateWindowEx(0x08000008, "tooltips_class32", "", 0x80000003, 0, 0, 0, 0, owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new Win32Exception();
        text = Marshal.StringToHGlobalUni("Codex Meter · 사용량 확인 중");
        // TOOLINFO_V2_SIZE also works without a Common Controls v6 activation context.
        tool = new ToolInfo { Size = (uint)Marshal.OffsetOf<ToolInfo>(nameof(ToolInfo.Reserved)).ToInt32(), Flags = 0x11, Window = owner, Id = owner, Text = text };
        if (SendMessage(handle, 0x0432, IntPtr.Zero, ref tool) == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("Tooltip registration failed"); }
        SendMessage(handle, 0x0418, IntPtr.Zero, new IntPtr((int)(440 * Math.Max(1, TaskbarNative.GetDpiForWindow(owner) / 96f))));
        SendMessage(handle, 0x0403, new IntPtr(3), new IntPtr(400));
        SendMessage(handle, 0x0403, new IntPtr(1), new IntPtr(100));
        SendMessage(handle, 0x0403, new IntPtr(2), new IntPtr(30000));
        TaskbarNative.SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013);
    }
    internal void Update(string value)
    {
        if (handle == IntPtr.Zero || value == content) return;
        var replacement = Marshal.StringToHGlobalUni(value);
        tool.Text = replacement;
        SendMessage(handle, 0x0439, IntPtr.Zero, ref tool);
        Marshal.FreeHGlobal(text);
        text = replacement; content = value;
    }
    internal void Hide() { if (handle != IntPtr.Zero) SendMessage(handle, 0x041C, IntPtr.Zero, IntPtr.Zero); }
    public void Dispose()
    {
        if (handle != IntPtr.Zero) { DestroyWindow(handle); handle = IntPtr.Zero; }
        if (text != IntPtr.Zero) { Marshal.FreeHGlobal(text); text = IntPtr.Zero; }
    }
}
