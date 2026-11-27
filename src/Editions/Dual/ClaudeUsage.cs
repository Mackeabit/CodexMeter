using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexMeter;

/// <summary>Claude usage as last reported by Claude Code. Values are passive: they change only when Claude Code reports them.</summary>
public sealed record ClaudeReading(IReadOnlyList<Quota> Windows, DateTimeOffset Updated, string Source)
{
    public const string Bucket = "claude";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    public Quota? FiveHour => Windows.FirstOrDefault(q => q.Minutes == 300);
    public Quota? Weekly => Windows.FirstOrDefault(q => q.Minutes == 10080);
    public bool IsStale(DateTimeOffset now) => now - Updated > StaleAfter;
    /// <summary>Windows whose reset has passed are unknown: usage may have refilled since the last report.</summary>
    public IReadOnlyList<Quota> Known(DateTimeOffset now) => Windows.Where(q => !q.ResetPending(now)).ToArray();
    /// <summary>Input for the shared alert policy: the reading time is the sample time, so old reports are rejected as stale.</summary>
    internal Snapshot AlertSnapshot() => new(Windows, Bucket, null, null, null, Updated);
}

public static class ClaudeUsage
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMeter", "claude-usage.json");
    /// <summary>Claude's 20%/5% alert history, separate from Codex's notifications-v1.json.</summary>
    public static string AlertsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMeter", "notifications-claude-v1.json");

    /// <summary>Extracts rate_limits from the JSON Claude Code passes to a statusLine command. Returns null when absent.</summary>
    public static ClaudeReading? ParseStatusLine(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return null;
        var windows = new List<Quota>();
        foreach (var (name, minutes) in new[] { ("five_hour", 300), ("seven_day", 10080) })
        {
            if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) continue;
            if (!TryPercent(w, out var used)) continue;
            windows.Add(new Quota(ClaudeReading.Bucket, "Claude", Math.Clamp(used, 0, 100), minutes, ReadTime(w, "resets_at")));
        }
        return windows.Count == 0 ? null : new ClaudeReading(windows, now, "statusline");
    }

    private static bool TryPercent(JsonElement w, out double used)
    {
        used = 0;
        foreach (var key in new[] { "used_percentage", "utilization" })
            if (w.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out used) && double.IsFinite(used)) return true;
        return false;
    }

    private static DateTimeOffset? ReadTime(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        try
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var seconds)) return DateTimeOffset.FromUnixTimeSeconds(seconds);
            if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return parsed;
        }
        catch (ArgumentOutOfRangeException) { }
        return null;
    }

    // Stored file: only percentages, reset times and the update time. No session, path or account data.
    private sealed record StoredWindow(int Minutes, double Used, long? ResetsAt);
    private sealed record Stored(int Schema, long Updated, string Source, StoredWindow[] Windows);

    public static string Serialize(ClaudeReading reading) => JsonSerializer.Serialize(new Stored(1, reading.Updated.ToUnixTimeSeconds(), reading.Source,
        reading.Windows.Select(q => new StoredWindow(q.Minutes ?? 0, q.Used, q.Reset?.ToUnixTimeSeconds())).ToArray()));

    public static ClaudeReading? Deserialize(string json)
    {
        var stored = JsonSerializer.Deserialize<Stored>(json);
        if (stored == null || stored.Schema != 1 || stored.Windows == null) return null;
        var windows = stored.Windows.Where(w => w.Minutes > 0 && double.IsFinite(w.Used))
            .Select(w => new Quota(ClaudeReading.Bucket, "Claude", Math.Clamp(w.Used, 0, 100), w.Minutes, w.ResetsAt is long r ? DateTimeOffset.FromUnixTimeSeconds(r) : null)).ToArray();
        return new ClaudeReading(windows, DateTimeOffset.FromUnixTimeSeconds(stored.Updated), stored.Source ?? "unknown");
    }

    public static void Write(ClaudeReading reading, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temp, Serialize(reading));
        File.Move(temp, path, true);
    }

    /// <summary>Short text Claude Code shows as its status line, so the hook stays useful in the terminal too.</summary>
    public static string StatusText(ClaudeReading? reading, DateTimeOffset now)
    {
        if (reading == null) return "Claude 사용량 대기 중";
        string Part(Quota? q, string name) => q == null ? "" : $"{name} {Math.Floor(q.Remaining).ToString("0", CultureInfo.InvariantCulture)}%";
        var parts = new[] { Part(reading.FiveHour, "5h"), Part(reading.Weekly, "주") }.Where(p => p.Length > 0);
        return "Claude " + string.Join(" · ", parts) + " 남음";
    }

    /// <summary>Entry point for <c>CodexMeter.exe --claude-statusline</c>. Never fails Claude Code: errors only skip the write.</summary>
    public static int RunStatusLine(TextReader input, TextWriter output, string? path = null)
    {
        ClaudeReading? reading = null;
        try
        {
            reading = ParseStatusLine(input.ReadToEnd(), DateTimeOffset.Now);
            if (reading != null) Write(reading, path);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        output.WriteLine(StatusText(reading, DateTimeOffset.Now));
        output.Flush();
        return 0;
    }
}

/// <summary>Reads the stored file only when it changes.</summary>
public sealed class ClaudeUsageStore
{
    private readonly string path;
    private DateTime lastWrite;
    public ClaudeReading? Current { get; private set; }
    public ClaudeUsageStore(string? path = null) { this.path = path ?? ClaudeUsage.DefaultPath; }
    public bool Refresh()
    {
        try
        {
            if (!File.Exists(path)) { bool had = Current != null; Current = null; lastWrite = default; return had; }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == lastWrite) return false;
            Current = ClaudeUsage.Deserialize(File.ReadAllText(path));
            lastWrite = stamp;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }
}
