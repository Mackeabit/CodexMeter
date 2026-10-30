using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace CodexMeter;

// Use the Unicode Shell API directly; WScript.Shell can reject non-ANSI
// targets on Windows installations whose system locale is not Korean.
internal static class WindowsShortcut
{
    internal static void Write(string file, string executable, string arguments)
    {
        object instance = new ShellLinkObject();
        try
        {
            var link = (IShellLinkW)instance;
            link.SetPath(executable);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetDescription("Codex Meter · 사용량 모니터");
            link.SetIconLocation(executable, 0);
            ((IPersistFile)instance).Save(Path.GetFullPath(file), true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    internal static (string Target, string Arguments, string Directory) Read(string file)
    {
        object instance = new ShellLinkObject();
        try
        {
            ((IPersistFile)instance).Load(Path.GetFullPath(file), 0);
            var link = (IShellLinkW)instance;
            var target = new StringBuilder(32768);
            var arguments = new StringBuilder(32768);
            var directory = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 4); // SLGP_RAWPATH
            link.GetArguments(arguments, arguments.Capacity);
            link.GetWorkingDirectory(directory, directory.Capacity);
            return (target.ToString(), arguments.ToString(), directory.ToString());
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLinkObject { }

    // Method order follows the native IShellLinkW vtable.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, IntPtr findData, uint flags);
        void GetIDList(out IntPtr itemList);
        void SetIDList(IntPtr itemList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
