using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using Microsoft.Win32;

namespace CodexMeter;

public static class StartupRegistration
{
    public const string LinkName = "Codex Meter.lnk";
    public static string Folder => Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    public static string Link => Path.Combine(Folder, LinkName);

    public static void WriteShortcut(string file, string executable, string arguments)
    {
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("실행 파일이 없습니다.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        WindowsShortcut.Write(file, executable, arguments);
        var saved = ReadShortcut(file);
        if (!string.Equals(saved.Target, executable, StringComparison.OrdinalIgnoreCase) || saved.Arguments != arguments)
            throw new IOException("바로가기 등록 결과를 확인하지 못했습니다.");
    }

    public static (string Target, string Arguments, string Directory) ReadShortcut(string file)
    {
        if (!File.Exists(file)) return ("", "", "");
        return WindowsShortcut.Read(file);
    }

    public static bool Registered { get { try { return File.Exists(ReadShortcut(Link).Target); } catch { return false; } } }
    public static bool DisabledByWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder");
            return key?.GetValue(LinkName) is byte[] state && state.Length > 0 && state[0] is 3 or 7;
        }
    }
    public static void Enable(string executable) => WriteShortcut(Link, executable, "--startup");
    public static void Disable() { if (File.Exists(Link)) File.Delete(Link); }
    public static string Status => !Registered ? "등록 안 됨" : DisabledByWindows ? "등록됨 · Windows에서 사용 안 함" : "등록됨 · Windows 로그인 시 실행";
}

public static class Installation
{
    public const string Version = "2.2.3";
#if EXPANDED
    public const string Edition = "Expanded";
#else
    public const string Edition = "Classic";
#endif
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexMeter");
    public static bool IsInstalled => IsInside(Environment.ProcessPath!, Root);
    private static bool IsInside(string path, string root) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static string? ReusableApplication(string source, string installRoot, string menuShortcut)
    {
        try
        {
            string target = StartupRegistration.ReadShortcut(menuShortcut).Target;
            if (!File.Exists(target) || !IsInside(target, installRoot)) return null;
            foreach (string file in new[] { "CodexMeter.dll", "CodexMeter.runtimeconfig.json", "CodexMeter.exe" })
            {
                using var original = File.OpenRead(Path.Combine(source, file));
                using var installed = File.OpenRead(Path.Combine(Path.GetDirectoryName(target)!, file));
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(installed))) return null;
            }
            // A removed runtime dependency must trigger repair, even for the same application version.
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(target)!, Path.GetRelativePath(source, file)))) return null;
            return target;
        }
        catch { return null; }
    }

    public static string CopyApplication(string source, string installRoot)
    {
        source = Path.GetFullPath(source);
        // Every install gets its own directory, so a running version is never overwritten.
        string destination = Path.Combine(Path.GetFullPath(installRoot), "versions", Version + "-" + Guid.NewGuid().ToString("N")[..8]);
        if (IsInside(destination, source)) throw new IOException("원본 폴더 안에는 설치할 수 없습니다.");
        foreach (string required in new[] { "CodexMeter.exe", "CodexMeter.dll", "CodexMeter.runtimeconfig.json", "coreclr.dll", "hostfxr.dll" })
            if (!File.Exists(Path.Combine(source, required))) throw new FileNotFoundException("압축을 모두 푼 뒤 Install.cmd를 실행하세요. 누락 파일: " + required);
        foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("링크가 포함된 폴더는 설치할 수 없습니다.");
            if (Directory.Exists(path)) continue;
            var target = Path.Combine(destination, Path.GetRelativePath(source, path));
            if (!IsInside(target, destination)) throw new IOException("설치 경로가 올바르지 않습니다.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target, false);
        }
        return Path.Combine(destination, "CodexMeter.exe");
    }

    public static int Run()
    {
        using var installLock = new Mutex(true, "Local\\CodexMeter.Install.v2", out bool first);
        if (!first) { MessageBox.Show("다른 설치가 진행 중입니다.", "Codex Meter"); return 1; }
        try
        {
            string menuShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), StartupRegistration.LinkName);
            string executable = IsInstalled ? Environment.ProcessPath! : ReusableApplication(AppContext.BaseDirectory, Root, menuShortcut) ?? CopyApplication(AppContext.BaseDirectory, Root);
            StartupRegistration.WriteShortcut(menuShortcut, executable, "");
            StartupRegistration.Enable(executable);
            try { using var stop = EventWaitHandle.OpenExisting("Local\\CodexMeter.Exit.v2"); stop.Set(); } catch (WaitHandleCannotBeOpenedException) { }
            bool running = true;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                using var appLock = new Mutex(false, "Local\\CodexMeter.v1");
                try { if (appLock.WaitOne(0)) { appLock.ReleaseMutex(); running = false; break; } }
                catch (AbandonedMutexException) { appLock.ReleaseMutex(); running = false; break; }
                Thread.Sleep(100);
            }
            string message = $"Codex Meter {Version} 설치와 자동 실행 등록을 마쳤습니다.\n\n다음 로그인부터 자동으로 실행됩니다.\n압축을 푼 원본 폴더는 옮겨도 됩니다.";
            if (StartupRegistration.DisabledByWindows) message = $"Codex Meter {Version} 설치와 바로가기 등록을 마쳤습니다.\n\nWindows가 자동 실행을 차단 중입니다. Windows 설정 → 앱 → 시작 프로그램에서 Codex Meter를 켜 주세요.";
            if (running) message += "\n\n구버전이 실행 중입니다. 트레이 메뉴에서 종료한 뒤 시작 메뉴의 Codex Meter를 실행하세요.";
            else Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! });
            MessageBox.Show(message, "Codex Meter · 설치 완료", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("설치를 완료하지 못했습니다.\n압축을 모두 풀었는지, 설치 폴더에 쓰기 권한이 있는지 확인하세요.\n\n오류: " + e.GetType().Name, "Codex Meter", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}
