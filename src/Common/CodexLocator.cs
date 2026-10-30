using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CodexMeter;

public static class CodexLocator
{
    public static string? SelectExisting(IEnumerable<string?> paths, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        foreach (var raw in paths.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string path = Environment.ExpandEnvironmentVariables(raw!.Trim().Trim('"'));
                // Only native CLI binaries are launched, never shell scripts or the desktop GUI.
                if (Path.IsPathFullyQualified(path) && string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase) && exists(path))
                    return Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static IEnumerable<string> FromPath(string? value)
    {
        foreach (var part in (value ?? "").Split(Path.PathSeparator))
        {
            string dir = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
            if (!string.IsNullOrWhiteSpace(dir) && Path.IsPathFullyQualified(dir)) yield return Path.Combine(dir, "codex.exe");
        }
    }

    public static IEnumerable<string> DesktopCandidates(string local)
    {
        yield return Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        yield return Path.Combine(local, "Programs", "Codex", "resources", "codex.exe");
        yield return Path.Combine(local, "Programs", "OpenAI", "Codex", "resources", "codex.exe");
        yield return Path.Combine(local, "OpenAI", "Codex", "bin", "codex.exe");
        foreach (var dir in Directories(Path.Combine(local, "OpenAI", "Codex", "bin")))
            yield return Path.Combine(dir, "codex.exe");
    }

    private static string[] Directories(string root, string pattern = "*")
    {
        try { return Directory.GetDirectories(root, pattern).OrderByDescending(Directory.GetLastWriteTimeUtc).ToArray(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return Array.Empty<string>(); }
    }

    public static string StoreBinary(string packageRoot) => Path.Combine(packageRoot, "app", "resources", "codex.exe");

    private static IEnumerable<string> AdditionalCandidates(string local, string roaming, string home)
    {
        var nodeRoots = new[] { Path.Combine(roaming, "npm", "node_modules", "@openai"), Path.Combine(local, "Programs", "nodejs", "node_modules", "@openai"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node_modules", "@openai") };
        foreach (var root in nodeRoots)
        {
            foreach (var arch in new[] { "x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc" })
            {
                yield return Path.Combine(root, "codex", "vendor", arch, "codex", "codex.exe");
                foreach (var package in new[] { "codex-win32-x64", "codex-win32-arm64" })
                {
                    yield return Path.Combine(root, package, "vendor", arch, "codex", "codex.exe");
                    yield return Path.Combine(root, "codex", "node_modules", "@openai", package, "vendor", arch, "codex", "codex.exe");
                }
            }
        }
        foreach (var editor in new[] { ".vscode", ".vscode-insiders", ".cursor" })
            foreach (var extension in Directories(Path.Combine(home, editor, "extensions"), "openai.chatgpt-*"))
                foreach (var arch in new[] { "windows-x86_64", "windows-aarch64" })
                    yield return Path.Combine(extension, "bin", arch, "codex.exe");
    }

    public static async Task<string> ResolveAsync(string? preferred, CancellationToken cancellationToken = default)
    {
        string? found = SelectExisting(new[] { preferred });
        if (found != null) return found;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = DesktopCandidates(local).ToList();
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            candidates.AddRange(FromPath(Environment.GetEnvironmentVariable("PATH", target)));
        found = SelectExisting(candidates);
        if (found != null) return found;

        // Store package paths include a version and must be queried on this PC, not baked into a ZIP.
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(powershell))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var proc = new Process { StartInfo = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Get-AppxPackage -Name OpenAI.Codex -ErrorAction SilentlyContinue | Sort-Object Version -Descending | ForEach-Object { $_.InstallLocation }" }) proc.StartInfo.ArgumentList.Add(arg);
            proc.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
            try
            {
                proc.Start();
                var output = proc.StandardOutput.ReadToEndAsync(timeout.Token);
                var errors = proc.StandardError.ReadToEndAsync(timeout.Token);
                await proc.WaitForExitAsync(timeout.Token);
                found = SelectExisting((await output).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(StoreBinary));
                await errors;
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }
            finally { try { if (!proc.HasExited) proc.Kill(true); } catch { } }
            if (found != null) return found;
        }
        found = SelectExisting(AdditionalCandidates(local, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        return found ?? throw new FileNotFoundException("Codex를 찾지 못했습니다. 이 PC에서 Codex를 설치·로그인한 뒤 ‘연결 및 자동 실행 설정’에서 다시 탐색하거나 codex.exe를 선택하세요.");
    }
}
