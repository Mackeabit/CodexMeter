using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexMeter;

/// <summary>
/// Asks the Claude Code CLI for current limits by sending one minimal Haiku request
/// (no tools, no MCP, no settings, short system prompt, no saved session) and reading
/// the rate_limit_event from its stream-json output. Each poll spends a small amount of usage.
/// </summary>
public sealed class ClaudePoller
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim gate = new(1, 1);
    public string? LastError { get; private set; }

    public static string? Locate()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var candidate = Path.Combine(dir.Trim(), "claude.exe"); if (File.Exists(candidate)) return candidate; } catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Reads both windows from stream-json lines. utilization is a 0..1 fraction.</summary>
    public static ClaudeReading? ParseStream(IEnumerable<string> lines, DateTimeOffset now)
    {
        ClaudeReading? found = null;
        foreach (var line in lines)
        {
            if (!line.Contains("rate_limit_event", StringComparison.Ordinal)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t) || t.GetString() != "rate_limit_event") continue;
                if (!root.TryGetProperty("rate_limit_info", out var info) || info.ValueKind != JsonValueKind.Object) continue;
                var windows = new List<Quota>();
                if (info.TryGetProperty("unifiedWindows", out var unified) && unified.ValueKind == JsonValueKind.Object)
                {
                    foreach (var (name, minutes) in new[] { ("five_hour", 300), ("seven_day", 10080) })
                        if (unified.TryGetProperty(name, out var w) && Window(w, minutes) is Quota q) windows.Add(q);
                }
                else if (info.TryGetProperty("rateLimitType", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    int? minutes = type.GetString() switch { "five_hour" => 300, "seven_day" => 10080, _ => null };
                    if (minutes is int m && Window(info, m) is Quota q) windows.Add(q);
                }
                if (windows.Count > 0) found = new ClaudeReading(windows, now, "poll");
            }
        }
        return found;
    }

    private static Quota? Window(JsonElement w, int minutes)
    {
        if (w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number || !u.TryGetDouble(out var fraction) || !double.IsFinite(fraction)) return null;
        DateTimeOffset? reset = null;
        if (w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var seconds))
            try { reset = DateTimeOffset.FromUnixTimeSeconds(seconds); } catch (ArgumentOutOfRangeException) { }
        return new Quota(ClaudeReading.Bucket, "Claude", Math.Clamp(fraction * 100, 0, 100), minutes, reset);
    }

    public async Task<ClaudeReading?> PollAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            LastError = null;
            var exe = Locate();
            if (exe == null) { LastError = "Claude Code CLI를 찾지 못했습니다."; return null; }
            // An empty private folder keeps project CLAUDE.md files and hooks out of the request.
            var work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMeter", "claude-probe");
            Directory.CreateDirectory(work);
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work, StandardOutputEncoding = System.Text.Encoding.UTF8 };
            foreach (var a in new[] { "-p", "--model", "haiku", "--output-format", "stream-json", "--verbose", "--max-turns", "1", "--tools=", "--system-prompt", "Reply with one word.", "--strict-mcp-config", "--setting-sources=", "--no-session-persistence" })
                info.ArgumentList.Add(a);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var proc = new Process { StartInfo = info };
            proc.Start();
            // Diagnostics are drained and discarded; nothing from the CLI is written to disk except the parsed limits.
            var drain = Task.Run(async () => { try { while (await proc.StandardError.ReadLineAsync(timeout.Token) != null) { } } catch { } });
            var lines = new List<string>();
            try
            {
                await proc.StandardInput.WriteLineAsync("ok");
                proc.StandardInput.Close();
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(timeout.Token)) != null) lines.Add(line);
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { LastError = "Claude 응답 시간이 초과되었습니다."; }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                await drain;
            }
            var reading = ParseStream(lines, DateTimeOffset.Now);
            if (reading == null) { LastError ??= proc.HasExited && proc.ExitCode != 0 ? "Claude 조회가 실패했습니다. 터미널에서 claude auth status를 확인하세요." : "Claude 사용량 정보가 응답에 없습니다."; return null; }
            LastError = null;
            ClaudeUsage.Write(reading);
            return reading;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            LastError = "Claude 조회를 실행하지 못했습니다.";
            return null;
        }
        finally { gate.Release(); }
    }
}
