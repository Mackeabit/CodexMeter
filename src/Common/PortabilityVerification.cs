using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexMeter;

public static class PortabilityVerification
{
    public static int Run(string[] args)
    {
        var folder = Path.GetFullPath(args.SkipWhile(x => x != "--verify-portability").Skip(1).First());
        Directory.CreateDirectory(folder);
        var checks = new List<string>();
        void Check(bool success, string name) { if (!success) throw new InvalidOperationException(name); checks.Add(name); }
        string root = Path.Combine(folder, "다른 PC 공백 경로 " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        string Make(string relative) { string path = Path.Combine(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture"); return path; }
        try
        {
            string first = Make("로컬 앱\\OpenAI\\Codex\\bin\\version-new\\codex.exe");
            string second = Make("사용자 경로\\bin\\codex.exe");
            Check(CodexLocator.SelectExisting(new[] { first, second }) == first, "explicit native executable takes precedence");
            Check(CodexLocator.SelectExisting(new[] { Path.Combine(root, "old", "codex.exe"), second }) == second, "missing override falls back to an existing candidate");
            Check(CodexLocator.SelectExisting(CodexLocator.DesktopCandidates(Path.Combine(root, "로컬 앱"))) == first, "versioned Desktop path works without PATH");
            Check(CodexLocator.SelectExisting(CodexLocator.FromPath(";\"" + Path.GetDirectoryName(second) + "\";;relative;")) == second, "quoted Korean paths and spaces are preserved");
            Check(!CodexLocator.FromPath(null).Any() && !CodexLocator.FromPath(";;relative;").Any(), "empty and relative PATH entries are ignored");
            Check(CodexLocator.SelectExisting(new[] { "codex.exe", Make("bin\\codex.cmd"), Make("gui\\CodexDesktop.exe") }) == null, "shell shims and desktop GUI are never executed as CLI");
            string storeRoot = Path.Combine(root, "WindowsApps", "OpenAI.Codex_99.0.0_x64__test");
            string store = Make(Path.GetRelativePath(root, CodexLocator.StoreBinary(storeRoot)));
            Check(CodexLocator.SelectExisting(new[] { CodexLocator.StoreBinary(storeRoot) }) == store, "Store app-server binary is resolved inside package resources");
            string source = Path.Combine(root, "배포 폴더", "app");
            foreach (string name in new[] { "CodexMeter.exe", "CodexMeter.dll", "CodexMeter.runtimeconfig.json", "coreclr.dll", "hostfxr.dll", "ko\\resource.dll" }) Make(Path.GetRelativePath(root, Path.Combine(source, name)));
            // Windows Shell may inspect the executable when assigning its icon.
            // Use a valid PE apphost instead of a text file with an .exe suffix.
            File.Copy(Environment.ProcessPath!, Path.Combine(source, "CodexMeter.exe"), true);
            string auth = Make("배포 폴더\\auth.json");
            string destination = Installation.CopyApplication(source, Path.Combine(root, "사용자 설치"));
            Check(File.Exists(destination) && File.Exists(Path.Combine(Path.GetDirectoryName(destination)!, "ko", "resource.dll")), "installer copies executable and nested runtime resources");
            Check(!File.Exists(Path.Combine(Path.GetDirectoryName(destination)!, "auth.json")) && File.Exists(auth), "installer copies only app contents and leaves external user files alone");
            string shortcut = Path.Combine(root, "시작프로그램", StartupRegistration.LinkName);
            StartupRegistration.WriteShortcut(shortcut, destination, "--startup");
            var link = StartupRegistration.ReadShortcut(shortcut);
            Check(link.Target == destination && link.Arguments == "--startup" && link.Directory == Path.GetDirectoryName(destination), "startup shortcut target, argument and working directory round-trip through Windows COM");
            string unicodeTarget = Make("한글 résumé 日本語 🌙\\CodexMeter.exe");
            File.Copy(Environment.ProcessPath!, unicodeTarget, true);
            string unicodeLink = Path.Combine(root, "다국어 바로가기 🌙.lnk");
            StartupRegistration.WriteShortcut(unicodeLink, unicodeTarget, "--startup");
            Check(StartupRegistration.ReadShortcut(unicodeLink).Target == unicodeTarget, "Unicode shortcut paths survive characters outside the system ANSI code page");
            Directory.Move(Path.GetDirectoryName(source)!, Path.Combine(root, "원본 이동 후"));
            Check(File.Exists(StartupRegistration.ReadShortcut(shortcut).Target), "moving extracted package does not break installed shortcut");
            string newDestination = Installation.CopyApplication(Path.GetDirectoryName(destination)!, Path.Combine(root, "사용자 설치"));
            Check(newDestination != destination && File.Exists(destination) && File.Exists(newDestination), "upgrade preserves existing files and uses a new version directory");
            StartupRegistration.WriteShortcut(shortcut, newDestination, "--startup");
            Check(StartupRegistration.ReadShortcut(shortcut).Target == newDestination, "upgrade retargets existing startup shortcut to new installation");
            Check(Installation.ReusableApplication(Path.GetDirectoryName(destination)!, Path.Combine(root, "사용자 설치"), shortcut) == newDestination, "reopening the same installer reuses an intact installation");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(newDestination)!, "CodexMeter.dll"), "changed");
            Check(Installation.ReusableApplication(Path.GetDirectoryName(destination)!, Path.Combine(root, "사용자 설치"), shortcut) == null, "changed application requires a new installation");
            var missing = Path.Combine(root, "미완성 app"); Directory.CreateDirectory(missing);
            bool rejected = false;
            try { Installation.CopyApplication(missing, Path.Combine(root, "사용자 설치")); } catch (FileNotFoundException) { rejected = true; }
            Check(rejected, "incomplete packages are rejected before startup registration");
            File.WriteAllText(Path.Combine(folder, "portability.json"), JsonSerializer.Serialize(new { passed = true, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(folder, "portability.json"), JsonSerializer.Serialize(new { passed = false, checks, error = e.Message }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }
}
