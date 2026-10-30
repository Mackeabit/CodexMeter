using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CodexMeter;

internal static class AlertVerification
{
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 16, 0, 0, TimeSpan.FromHours(9));
    private static Quota Q(double remaining, int minutes = 10080, DateTimeOffset? reset = null) =>
        new("codex", "Codex", 100 - remaining, minutes, reset ?? Start.AddDays(1));
    private static Snapshot S(DateTimeOffset time, params Quota[] quotas) =>
        new(quotas, "test-plan", null, true, "verification-account", time);

    public static int Run(string[] args)
    {
        string folder = Path.GetFullPath(args.SkipWhile(x => x != "--verify-alerts").Skip(1).First());
        Directory.CreateDirectory(folder);
        string fixtures = Path.Combine(folder, "fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtures);
        var checks = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidOperationException(name); checks.Add(name); }
        string FileFor(string name) => Path.Combine(fixtures, name + ".json");
        UsageAlerts Policy(string name) => new(FileFor(name));
        var now = Start;
        Snapshot Fresh(params Quota[] quotas) => S(now, quotas);
        UsageNotification? Observe(UsageAlerts p, params Quota[] quotas) => p.Observe(Fresh(quotas), now, true);
        try
        {
            var p = Policy("thresholds");
            Check(Observe(p, Q(21)) == null && Observe(p, Q(20)) == null, "exactly 20 percent does not warn");
            Check(Observe(p, Q(19)) is { Critical: false, Warnings.Count: 1 }, "first reading below 20 percent warns once");
            Check(Enumerable.Range(0, 10).All(_ => Observe(p, Q(19)) == null), "ten repeated low readings are silent");
            Check(Observe(p, Q(5)) == null, "exactly 5 percent does not trigger the critical threshold");
            Check(Observe(p, Q(4)) is { Critical: true, Warnings.Count: 1 }, "crossing below 5 percent warns once more");
            Check(Observe(p, Q(3)) == null && Observe(p, Q(18)) == null && Observe(p, Q(4)) == null,
                "fluctuations and recovery within one cycle do not repeat either stage");
            Check(Observe(new UsageAlerts(FileFor("thresholds")), Q(2)) == null, "both warning stages survive policy reload");
            var jump = Policy("jump");
            Observe(jump, Q(21));
            Check(Observe(jump, Q(4)) is { Critical: true, Warnings.Count: 1 } && Observe(jump, Q(19)) == null,
                "21 to 4 percent produces only one critical notification and consumes lower stage");
            var firstLow = Policy("initial");
            Check(Observe(firstLow, Q(3)) is { Critical: true } && Observe(firstLow, Q(3)) == null,
                "first launch already low sends one relevant warning");
            var precise = Observe(Policy("precision"), Q(4.99));
            Check(precise != null && precise.Text.Contains("4.9%") && precise.Title.Contains("5% 미만"),
                "percentage formatting never rounds a sub-threshold value up to its boundary");

            var dual = Policy("dual");
            var shortQuota = Q(19, 300, Start.AddHours(2));
            var weekly = Q(17);
            var combined = Observe(dual, weekly, shortQuota);
            Check(combined is { Warnings.Count: 2, Critical: false } && combined.Warnings[0].Quota.Minutes == 300,
                "simultaneous quota warnings are merged and ordered short then weekly");
            Check(combined!.Text.Contains("5시간 19%") && combined.Text.Contains("주간 17%") &&
                combined.Text.Contains(shortQuota.Reset!.Value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)),
                "one notification includes each quota, remaining amount and numeric local reset date");
            Check(Observe(dual, Q(18, 300, shortQuota.Reset), Q(19)) == null &&
                Observe(dual, Q(19, 300, shortQuota.Reset), Q(16)) == null,
                "changing the most restrictive quota does not repeat earlier warnings");
            Check(Observe(dual, Q(4, 300, shortQuota.Reset), Q(3)) is { Critical: true, Warnings.Count: 2 },
                "simultaneous critical stages also produce one merged notification");
            var mixed = Observe(Policy("mixed"), Q(4, 300, shortQuota.Reset), Q(19));
            Check(mixed is { Warnings.Count: 2, Critical: true }, "mixed thresholds are merged with critical priority");
            Check(Observe(Policy("buckets"), Q(19), Q(1, 60) with { Bucket = "other" }) is { Warnings.Count: 1 },
                "Codex quota notifications exclude unrelated buckets when Codex is available");
            Check(Observe(Policy("fallback"), Q(19) with { Bucket = "other" }) is { Warnings.Count: 1 },
                "legacy or alternative single bucket remains supported");
            Check(Observe(Policy("duplicates"), Q(19), Q(19)) is { Warnings.Count: 1 }, "duplicate quota rows cannot duplicate notifications");

            var drift = Policy("drift");
            Observe(drift, Q(19));
            Check(Enumerable.Range(1, 15).All(i => Observe(drift, Q(18, reset: Start.AddDays(1).AddSeconds(i * 17))) == null),
                "small changes to reset timestamp do not rearm the warning");
            Check(Observe(drift, Q(17, reset: Start.AddDays(1).ToOffset(TimeSpan.Zero))) == null,
                "equivalent timestamp in another timezone does not rearm");
            Check(Observe(drift, Q(16, reset: Start.AddDays(8))) == null, "large future timestamp change without refill does not rearm early");
            now = Start.AddDays(1).AddMinutes(1);
            Check(Observe(drift, Q(15, reset: Start.AddDays(1))) == null,
                "passing the advertised boundary without a new server cycle remains silent");
            Check(Observe(drift, Q(14, reset: Start.AddDays(8))) is { Warnings.Count: 1 } &&
                Observe(drift, Q(13, reset: Start.AddDays(8))) == null,
                "confirmed next cycle after offline time rearms exactly once even if already low");
            now = Start;
            var early = Policy("manual-reset");
            Observe(early, Q(19));
            Check(Observe(early, Q(90, reset: Start.AddDays(8))) == null &&
                Observe(early, Q(19, reset: Start.AddDays(8))) is { Warnings.Count: 1 },
                "clear refill with an advanced reset supports an early manual reset");
            var unknown = Policy("no-reset");
            Observe(unknown, Q(19) with { Reset = null });
            Check(Observe(unknown, Q(21) with { Reset = null }) == null &&
                Observe(unknown, Q(19) with { Reset = null }) == null, "unknown reset and 19 to 21 fluctuation do not rearm");
            Observe(unknown, Q(90) with { Reset = null });
            Check(Observe(unknown, Q(19) with { Reset = null })?.Text.Contains("리셋 시간 미제공") == true,
                "strong refill can confirm a cycle without supplied reset timestamps");

            var mute = Policy("mute");
            Observe(mute, shortQuota, weekly);
            Check(mute.SetMuted(Fresh(shortQuota, weekly), now, true) && mute.AnyMuted(Fresh(shortQuota, weekly)),
                "next-reset pause is available for both current quota cycles");
            mute = new UsageAlerts(FileFor("mute"));
            Check(mute.AnyMuted(Fresh(shortQuota, weekly)) && Observe(mute, Q(3, 300, shortQuota.Reset), Q(3)) == null,
                "pause persists across restart and suppresses critical warnings");
            now = Start.AddHours(3);
            var nextShort = Q(19, 300, Start.AddHours(7));
            Check(Observe(mute, nextShort, Q(3)) is { Warnings.Count: 1 } warning && warning.Warnings[0].Quota.Minutes == 300,
                "5-hour reset resumes only its own warnings while weekly stays muted");
            Check(mute.AnyMuted(Fresh(nextShort, Q(3))), "weekly pause survives another quota reset");
            Check(mute.SetMuted(Fresh(nextShort, Q(3)), now, false) && !mute.AnyMuted(Fresh(nextShort, Q(3))),
                "manual resume clears current pauses");
            Check(Observe(mute, nextShort, Q(3)) is { Warnings.Count: 1 } resumed && resumed.Warnings[0].Quota.Minutes == 10080,
                "manual resume allows missed critical stage without repeating an already delivered stage");
            Check(Observe(mute, nextShort, Q(3)) == null, "resumed alert is recorded normally");
            now = Start;
            var mutedBeforeLow = Policy("muted-before-low");
            Observe(mutedBeforeLow, Q(60));
            mutedBeforeLow.SetMuted(Fresh(Q(60)), now, true);
            Check(Observe(mutedBeforeLow, Q(19)) == null, "pause before first warning suppresses that warning");
            now = Start.AddDays(1).AddMinutes(1);
            Check(Observe(mutedBeforeLow, Q(19, reset: Start.AddDays(8))) is { Warnings.Count: 1 },
                "next confirmed cycle clears pause even when old cycle never warned");

            now = Start;
            var disabled = Policy("disabled");
            Check(disabled.Observe(Fresh(Q(19)), now, false) == null && Observe(disabled, Q(19)) is { Warnings.Count: 1 },
                "disabling alerts does not consume undelivered warning stages");
            Check(disabled.Observe(Fresh(Q(18)), now, false) == null && Observe(disabled, Q(18)) == null,
                "turning alerts off and on cannot repeat a recorded stage");
            now = Start.AddDays(1).AddMinutes(1);
            disabled.Observe(Fresh(Q(19, reset: Start.AddDays(8))), now, false);
            Check(Observe(disabled, Q(19, reset: Start.AddDays(8))) is { Warnings.Count: 1 },
                "cycles continue to advance while notifications are disabled");

            now = Start;
            var freshness = Policy("freshness");
            Check(freshness.Observe(null, now, true) == null && Observe(freshness) == null,
                "missing and empty responses do not invent a notification");
            Check(freshness.Observe(S(now.AddMinutes(-10), Q(19)), now, true) == null &&
                freshness.Observe(S(now.AddMinutes(10), Q(19)), now, true) == null && Observe(freshness, Q(19)) != null,
                "stale or future-dated samples do not consume the first fresh warning");
            Check(Observe(Policy("invalid"), Q(19) with { Used = double.NaN }) == null, "invalid percentage cannot enter alert history");
            Check(Observe(Policy("expired-main"), Q(2, 300, now.AddMinutes(-1)), Q(19)) is { Warnings.Count: 1 } valid &&
                valid.Warnings[0].Quota.Minutes == 10080, "expired short quota does not suppress a valid weekly warning");
            var ordered = Policy("order");
            ordered.Observe(S(now, Q(19)), now, true);
            Check(ordered.Observe(S(now.AddMinutes(-1), Q(4)), now, true) == null && Observe(ordered, Q(4)) != null,
                "out-of-order samples cannot consume a critical warning");

            var account = Policy("accounts");
            var anonymous = Fresh(Q(19)) with { Account = null };
            Check(account.Observe(anonymous, now, true) != null && account.Observe(Fresh(Q(19)), now, true) == null,
                "initial anonymous history migrates when an account ID becomes available");
            Check(account.Observe(Fresh(Q(19)) with { Account = "second-account" }, now, true) != null &&
                account.Observe(anonymous, now, true) == null && account.Observe(Fresh(Q(19)), now, true) == null,
                "explicit account switches keep independent histories and missing IDs preserve current history");
            string saved = File.ReadAllText(FileFor("accounts"));
            Check(!saved.Contains("verification-account") && !saved.Contains("second-account"), "persisted history contains no raw account IDs");
            var legacyPlan = Policy("plan");
            Observe(legacyPlan, Q(19));
            Check(legacyPlan.Observe(Fresh(Q(19)) with { Plan = "pro" }, now, true) == null,
                "plan label changes do not create new quota cycles");

            string blockedPath = FileFor("blocked");
            Directory.CreateDirectory(blockedPath);
            var blocked = new UsageAlerts(blockedPath);
            Check(Observe(blocked, Q(19)) == null && Observe(blocked, Q(19)) == null && blocked.StorageWarning != null,
                "failed persistent writes suppress delivery instead of flooding notifications");
            Directory.Move(blockedPath, blockedPath + ".fixture");
            Check(Observe(blocked, Q(19)) != null && Observe(blocked, Q(19)) == null && blocked.StorageWarning == null,
                "restored storage delivers once and clears its status");
            File.WriteAllText(FileFor("corrupt"), "{corrupt");
            var corrupt = Policy("corrupt");
            Check(Observe(corrupt, Q(19)) == null && corrupt.StorageWarning != null && File.ReadAllText(FileFor("corrupt")) == "{corrupt",
                "corrupt history is preserved and reported without crashing or repeating alerts");
            File.WriteAllText(FileFor("schema"), "{\"Schema\":999}");
            var schema = Policy("schema");
            Check(Observe(schema, Q(19)) == null && schema.StorageWarning != null, "unknown future schema is not silently overwritten");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ko-KR");
                var n = Observe(Policy("culture"), Q(19));
                Check(n != null && n.Text.Contains(Q(19).Reset!.Value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)),
                    "date separators remain numeric and consistent under Korean culture");
            }
            finally { CultureInfo.CurrentCulture = culture; }
            var many = Observe(Policy("many"), Enumerable.Range(1, 8).Select(i => Q(19, i * 60)).ToArray());
            Check(many != null && many.Text.Length < 255 && many.Text.Contains("외 5개 한도"),
                "large quota sets remain one notification within balloon text limits");
            File.WriteAllText(Path.Combine(folder, "alerts.json"), JsonSerializer.Serialize(new {
                passed = true, version = Installation.Version, checks,
                notificationExample = combined, criticalExample = mixed,
                note = "Synthetic data only; no real account requests, settings edits or OS notifications."
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(folder, "alerts.json"), JsonSerializer.Serialize(new {
                passed = false, version = Installation.Version, checks, error = e.Message
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    // Invoked in separate processes/editions against an explicit fixture path, never real preferences.
    public static int Probe(string[] args)
    {
        var values = args.SkipWhile(x => x != "--verify-alert-probe").Skip(1).ToArray();
        string folder = Path.GetFullPath(values[0]);
        string stage = values[1];
        Directory.CreateDirectory(folder);
        bool critical = stage.StartsWith("critical", StringComparison.Ordinal);
        var now = critical ? Start.AddMinutes(2) : Start;
        var policy = new UsageAlerts(Path.Combine(folder, "shared-history.json"));
        var notification = policy.Observe(S(now, Q(critical ? 4 : 19)), now, true);
        bool expected = stage is "first" or "critical";
        bool passed = (notification != null) == expected && policy.StorageWarning == null;
        File.WriteAllText(Path.Combine(folder, $"probe-{stage}-{Installation.Version}.json"), JsonSerializer.Serialize(new {
            passed, version = Installation.Version, stage, emitted = notification != null,
            critical = notification?.Critical, storageWarning = policy.StorageWarning
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
