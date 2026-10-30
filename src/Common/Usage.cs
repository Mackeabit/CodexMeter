using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexMeter;

public sealed record Quota(string Bucket, string Label, double Used, int? Minutes, DateTimeOffset? Reset)
{
    public double Remaining => Math.Clamp(100 - Used, 0, 100);
    public string Duration => Minutes switch { 10080 => "주간", 300 => "5시간", null => "사용량", _ when Minutes % 1440 == 0 => $"{Minutes / 1440}일", _ when Minutes % 60 == 0 => $"{Minutes / 60}시간", _ => $"{Minutes}분" };
    public bool ResetPending(DateTimeOffset now) => Reset.HasValue && Reset.Value <= now;
    public string Countdown(DateTimeOffset now)
    {
        if (Reset is null) return "리셋 시간 미제공";
        var span = Reset.Value - now;
        if (span.TotalSeconds <= 0) return "리셋 반영 확인 중";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}일 {span.Hours}시간 후 리셋";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}시간 {span.Minutes}분 후 리셋";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}분 후 리셋";
    }
}

public sealed record Snapshot(IReadOnlyList<Quota> Windows, string Plan, int? Resets, bool? Allowed, string? Account, DateTimeOffset Fetched)
{
    public Quota? Main => (Windows.Any(x => x.Bucket == "codex") ? Windows.Where(x => x.Bucket == "codex") : Windows).OrderBy(x => x.Remaining).FirstOrDefault();
    public static Snapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out var result)) root = result;
        var windows = new List<Quota>();
        string plan = "계정", fallbackPlan = "계정";
        bool legacy = true;
        void Bucket(JsonElement bucket, string key)
        {
            if (bucket.ValueKind != JsonValueKind.Object) return;
            var label = Str(bucket, "limitName") ?? key;
            var bucketPlan = Str(bucket, "planType");
            if (bucketPlan != null) { fallbackPlan = bucketPlan; if (key == "codex") plan = bucketPlan; }
            foreach (var slot in new[] { "primary", "secondary" })
            {
                if (!bucket.TryGetProperty(slot, out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("usedPercent", out var u) || u.ValueKind != JsonValueKind.Number || !u.TryGetDouble(out var used) || !double.IsFinite(used)) continue;
                int? minutes = w.TryGetProperty("windowDurationMins", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var mv) && mv > 0 ? mv : null;
                DateTimeOffset? reset = null;
                if (w.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var rv))
                    try { reset = DateTimeOffset.FromUnixTimeSeconds(rv); } catch (ArgumentOutOfRangeException) { }
                windows.Add(new Quota(key, label, Math.Clamp(used, 0, 100), minutes, reset));
            }
        }
        if (root.TryGetProperty("rateLimitsByLimitId", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in map.EnumerateObject()) { Bucket(p.Value, p.Name); legacy = false; }
        }
        if (legacy && root.TryGetProperty("rateLimits", out var old)) Bucket(old, Str(old, "limitId") ?? "codex");
        int? resets = null;
        if (root.TryGetProperty("rateLimitResetCredits", out var c) && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("availableCount", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var nv)) resets = nv;
        bool? allowed = root.TryGetProperty("ordinaryUsageAllowed", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null;
        return new Snapshot(windows, plan == "계정" ? fallbackPlan : plan, resets, allowed, Str(root, "accountId"), DateTimeOffset.Now);
    }
    private static string? Str(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed class UsageClient
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public string? PreferredPath { get; set; }
    public string? ResolvedPath { get; private set; }
    public async Task<Snapshot> ReadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(cancellationToken); }
        finally { gate.Release(); }
    }
    private async Task<Snapshot> ReadCoreAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        var token = timeout.Token;
        token.ThrowIfCancellationRequested();
        ResolvedPath = null;
        ResolvedPath = await CodexLocator.ResolveAsync(PreferredPath, token);
        var info = new ProcessStartInfo(ResolvedPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        info.ArgumentList.Add("app-server");
        info.ArgumentList.Add("--listen");
        info.ArgumentList.Add("stdio://");
        using var proc = new Process { StartInfo = info };
        proc.Start();
        // Drain diagnostic output without storing credentials, account details, or raw responses.
        var drain = Task.Run(async () => { try { while (await proc.StandardError.ReadLineAsync(token) != null) { } } catch { } });
        async Task Send(object message) { await proc.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token); await proc.StandardInput.FlushAsync(token); }
        async Task<string> Receive(int id)
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(token) ?? throw new IOException("Codex 연결이 종료되었습니다.");
                JsonDocument parsed;
                try { parsed = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (parsed)
                {
                    var obj = parsed.RootElement;
                    if (!obj.TryGetProperty("id", out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var num) || num != id) continue;
                    if (obj.TryGetProperty("error", out _)) throw new InvalidOperationException("사용량 조회가 거절되었습니다. Codex의 로그인 상태와 네트워크를 확인하세요.");
                    if (obj.TryGetProperty("result", out var result)) return result.GetRawText();
                }
            }
        }
        try
        {
            await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "codex_meter", title = "Codex Meter", version = Installation.Version } } });
            await Receive(1);
            await Send(new { method = "initialized", @params = new { } });
            await Send(new { id = 2, method = "account/rateLimits/read" });
            return Snapshot.Parse(await Receive(2));
        }
        finally
        {
            try { proc.StandardInput.Close(); } catch { }
            try { if (!proc.HasExited) { using var stop = new CancellationTokenSource(1500); await proc.WaitForExitAsync(stop.Token); } } catch { }
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            timeout.Cancel();
            await drain;
        }
    }
}
