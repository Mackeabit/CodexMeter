using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexMeter;

internal sealed record UsageWarning(Quota Quota, int Level);

internal sealed record UsageNotification(IReadOnlyList<UsageWarning> Warnings)
{
    public bool Critical => Warnings.Any(x => x.Level == 2);
    public string Title => Critical ? "Codex 잔여량 5% 미만" : "Codex 잔여량 20% 미만";
    public string Text => string.Join(Environment.NewLine, Warnings.Take(3).Select(w =>
        $"{w.Quota.Duration} {(Math.Floor(w.Quota.Remaining * 10) / 10).ToString("0.#", CultureInfo.InvariantCulture)}% 남음 · " +
        (w.Quota.Reset is { } reset
            ? reset.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) + " 리셋"
            : "리셋 시간 미제공"))) + (Warnings.Count > 3 ? $"\n외 {Warnings.Count - 3}개 한도" : "");
}

internal sealed class AlertCycle
{
    public DateTimeOffset? Reset { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public double LastRemaining { get; set; }
    public int NotifiedLevel { get; set; }
    public bool Muted { get; set; }
}

internal sealed class AlertHistory
{
    public int Schema { get; set; } = 1;
    public string Account { get; set; } = "local-profile";
    public Dictionary<string, AlertCycle> Cycles { get; set; } = new();
}

// Shared by both editions; no raw account IDs, credentials or server responses are stored.
internal sealed class UsageAlerts
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexMeter", "notifications-v1.json");
    private readonly string? path;
    private AlertHistory history = new();
    private bool loadBlocked;
    public string? StorageWarning { get; private set; }

    // A null path is an isolated in-memory policy for UI verification.
    public UsageAlerts(string? path) { this.path = path; Load(); }

    private void Load()
    {
        try
        {
            if (path != null && File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AlertHistory>(File.ReadAllText(path));
                if (loaded == null || loaded.Schema != 1 || loaded.Cycles == null ||
                    string.IsNullOrEmpty(loaded.Account) || loaded.Cycles.Any(p =>
                        p.Value == null || !double.IsFinite(p.Value.LastRemaining) ||
                        p.Value.NotifiedLevel is < 0 or > 2)) throw new JsonException();
                history = loaded;
            }
            loadBlocked = false;
            StorageWarning = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            loadBlocked = true;
            StorageWarning = "알림 기록을 읽을 수 없어 알림을 보류합니다.";
        }
    }

    private bool Ready()
    {
        if (loadBlocked) Load();
        return !loadBlocked;
    }

    private bool Commit(string before)
    {
        try
        {
            if (path != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                string serialized = JsonSerializer.Serialize(history);
                if (serialized != before || !File.Exists(path))
                {
                    File.WriteAllText(path + ".tmp", serialized);
                    File.Move(path + ".tmp", path, true);
                }
            }
            StorageWarning = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            history = JsonSerializer.Deserialize<AlertHistory>(before)!;
            StorageWarning = "알림 기록을 저장할 수 없어 알림을 보류합니다.";
            return false;
        }
    }

    private static string AccountKey(string account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)));
    private string CurrentAccount(Snapshot snapshot) => string.IsNullOrWhiteSpace(snapshot.Account)
        ? history.Account : AccountKey(snapshot.Account);
    private string SelectAccount(Snapshot snapshot)
    {
        string account = CurrentAccount(snapshot);
        // An initially anonymous response gaining an ID is not itself a new quota cycle.
        if (history.Account == "local-profile" && account != history.Account)
        {
            foreach (var pair in history.Cycles.Where(x => x.Key.StartsWith("local-profile:", StringComparison.Ordinal)).ToArray())
            {
                history.Cycles.TryAdd(account + pair.Key["local-profile".Length..], pair.Value);
                history.Cycles.Remove(pair.Key);
            }
        }
        history.Account = account;
        return account;
    }

    private static string Key(string account, Quota q) =>
        account + ":" + q.Bucket + ":" + (q.Minutes?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
    private static Quota[] Rows(Snapshot snapshot)
    {
        var rows = snapshot.Windows.Any(x => x.Bucket == "codex")
            ? snapshot.Windows.Where(x => x.Bucket == "codex") : snapshot.Windows;
        return rows.Where(x => double.IsFinite(x.Used))
            .GroupBy(x => Key("", x)).Select(g => g.OrderBy(x => x.Remaining).First())
            .OrderBy(x => x.Minutes ?? int.MaxValue).ThenBy(x => x.Bucket, StringComparer.Ordinal).ToArray();
    }

    private AlertCycle Cycle(string account, Quota q, DateTimeOffset now)
    {
        string key = Key(account, q);
        if (!history.Cycles.TryGetValue(key, out var cycle))
        {
            cycle = new AlertCycle { Reset = q.Reset, LastSeen = DateTimeOffset.MinValue, LastRemaining = q.Remaining };
            history.Cycles.Add(key, cycle);
        }
        return cycle;
    }

    private static void ObserveCycle(AlertCycle cycle, Quota q, DateTimeOffset now)
    {
        // Reset timestamps may drift. A new timestamp alone must never repeat a warning.
        double significantMinutes = Math.Max(5, (q.Minutes ?? 60) / 2d);
        bool advanced = cycle.Reset is { } oldReset && q.Reset is { } nextReset &&
            nextReset > now && (nextReset - oldReset).TotalMinutes >= significantMinutes;
        bool oldEnded = cycle.Reset.HasValue && cycle.Reset.Value <= now;
        bool recovered = q.Remaining >= 30 && q.Remaining - cycle.LastRemaining >= 10;
        bool recoveredWithoutBoundary = (cycle.Reset == null || oldEnded) &&
            cycle.LastRemaining < 20 && q.Remaining >= 80 && !q.ResetPending(now);
        bool resetConfirmed = (advanced && (oldEnded || recovered)) || recoveredWithoutBoundary;
        if (resetConfirmed)
        {
            cycle.NotifiedLevel = 0;
            cycle.Muted = false;
            cycle.Reset = q.Reset;
        }
        else if (q.Reset.HasValue && (cycle.Reset == null ||
            Math.Abs((q.Reset.Value - cycle.Reset.Value).TotalMinutes) < significantMinutes))
        {
            cycle.Reset = q.Reset;
        }
        cycle.LastSeen = now;
        cycle.LastRemaining = q.Remaining;
    }

    // Call only for successful fetches. The age guard also rejects accidentally cached input.
    public UsageNotification? Observe(Snapshot? snapshot, DateTimeOffset now, bool enabled,
        double maxAgeSeconds = 180)
    {
        if (snapshot == null || snapshot.Fetched > now.AddMinutes(1) ||
            now - snapshot.Fetched > TimeSpan.FromSeconds(maxAgeSeconds) || !Ready()) return null;
        string before = JsonSerializer.Serialize(history);
        string account = SelectAccount(snapshot);
        var warnings = new List<UsageWarning>();
        foreach (var q in Rows(snapshot))
        {
            var cycle = Cycle(account, q, now);
            // Out-of-order responses must not roll the cycle or usage reading backwards.
            if (snapshot.Fetched < cycle.LastSeen) continue;
            ObserveCycle(cycle, q, snapshot.Fetched);
            int level = q.Remaining < 5 ? 2 : q.Remaining < 20 ? 1 : 0;
            if (!enabled || cycle.Muted || q.ResetPending(now) || level <= cycle.NotifiedLevel) continue;
            cycle.NotifiedLevel = level; // Critical consumes the lower threshold as well.
            warnings.Add(new UsageWarning(q, level));
        }
        // Persist before notifying, so another process/edition cannot repeat the same warning.
        if (!Commit(before) || warnings.Count == 0) return null;
        return new UsageNotification(warnings);
    }

    public bool HasQuotas(Snapshot? snapshot) => snapshot != null && Rows(snapshot).Length != 0;
    public bool AnyMuted(Snapshot? snapshot) => snapshot != null && Rows(snapshot).Any(q =>
        history.Cycles.TryGetValue(Key(CurrentAccount(snapshot), q), out var c) && c.Muted);

    public bool SetMuted(Snapshot? snapshot, DateTimeOffset now, bool muted)
    {
        if (!HasQuotas(snapshot) || !Ready()) return false;
        string before = JsonSerializer.Serialize(history);
        string account = SelectAccount(snapshot!);
        foreach (var q in Rows(snapshot!)) Cycle(account, q, now).Muted = muted;
        return Commit(before);
    }
}
