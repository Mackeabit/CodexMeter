using System;
using System.IO;
using System.Linq;

namespace CodexMeter;

internal static class DesignVerification
{
    // The five states agreed for the center number: normal, no 5-hour plan, weekly low, 5-hour exhausted, weekly exhausted.
    private static void StateChecks(string folder, DateTimeOffset now, Quota week, Quota first, Snapshot codex, ClaudeReading claude, Action<bool, string> check)
    {
        RingService Codex(params Quota[] windows) => TaskbarHost.Services(codex with { Windows = windows }, false, claude, now)[0];

        var noFive = Codex(week);
        check(noFive.NoFiveHour && noFive.Shown == noFive.Weekly && noFive.Center == "37" && !noFive.Blocked,
            "a plan without a 5-hour limit shows its weekly value and stays usable");
        var unknownFive = TaskbarHost.Services(codex, false, claude with { Windows = new[] { claude.Windows[0] with { Reset = now.AddMinutes(-1) }, claude.Windows[1] } }, now)[1];
        check(!unknownFive.NoFiveHour && unknownFive.FiveHour == null && unknownFive.Center == "46",
            "a 5-hour value that is only unknown (reset passed) is not mistaken for a plan without that limit");

        var weeklyLow = Codex(first with { Used = 10 }, week with { Used = 88 });
        check(weeklyLow.Center == "90" && !weeklyLow.Alert && !weeklyLow.Blocked, "a low weekly value does not take over the center while the 5-hour window has room");

        var fiveGone = Codex(first with { Used = 100 }, week with { Used = 40 });
        var fiveToken = RingArtwork.Token(fiveGone, fiveGone.Shown!, now);
        check(fiveGone.Shown == fiveGone.Weekly && fiveGone.Center == "60" && fiveGone.Blocked && fiveGone.Alert && fiveGone.Blocking == fiveGone.FiveHour &&
            fiveToken.Top == RingArtwork.BlockedToken && fiveToken.Bottom == RingArtwork.ShortCountdown(first, now),
            "an exhausted 5-hour window shows the weekly value, marks the service unusable and counts down to the 5-hour reset");

        var weekGone = Codex(first with { Used = 10 }, week with { Used = 100 });
        check(weekGone.Center == "90" && weekGone.Blocked && weekGone.Blocking == weekGone.Weekly && RingArtwork.Token(weekGone, weekGone.Shown!, now).Bottom == "1일",
            "an exhausted weekly window marks the service unusable until the weekly reset while the center keeps the 5-hour value");

        var bothGone = Codex(first with { Used = 100 }, week with { Used = 100 });
        check(bothGone.Blocking == bothGone.Weekly, "with both windows exhausted, use resumes at the later reset");
        check(!(Codex(first, week) with { Warning = true }).Blocked, "a server denial keeps its own warning instead of the blocked state");
        var resetPassed = Codex(first with { Used = 100, Reset = now.AddMinutes(-1) }, week with { Used = 40 });
        check(!resetPassed.Blocked && resetPassed.Center == "60",
            "an exhausted 5-hour window whose reset time has passed no longer marks the service unusable (offline or before the next refresh)");
        var claudeWeeklyOnly = TaskbarHost.Services(codex, false, claude with { Windows = new[] { claude.Windows[1] } }, now)[1];
        check(!claudeWeeklyOnly.NoFiveHour && claudeWeeklyOnly.Center == "46",
            "a Claude reading with only a weekly window keeps 5-hour unknown instead of claiming the account has none");

        // Geometry: the inner (5-hour) track disappears for a plan without that limit.
        int InnerPixels(RingService s)
        {
            using var image = RingArtwork.Render(RingArtwork.PreferredWidth, 44, 1, new[] { s, s }, false, now);
            // A point on the inner ring's left edge, between the center digits and the outer ring.
            // At 1x: center (22, 22), outer stroke x 4..7.6, inner stroke x 9.9..13, digits start near x 15.
            return Enumerable.Range(10, 3).Sum(x => Enumerable.Range(20, 4).Count(y => image.GetPixel(x, y).A > 40));
        }
        check(InnerPixels(Codex(first, week)) > 0 && InnerPixels(noFive) == 0, "a plan without a 5-hour limit draws no inner ring");

        TaskbarHost.SavePreview(Path.Combine(folder, "rings-five-hour-exhausted.png"), codex with { Windows = new[] { first with { Used = 100 }, week with { Used = 40 } } }, claude, false, 1.5f, light: false, now: now);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-weekly-exhausted.png"), codex with { Windows = new[] { first with { Used = 10 }, week with { Used = 100 } } }, claude, false, 1.5f, light: false, now: now);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-no-five-hour.png"), codex with { Windows = new[] { week } }, claude, false, 1.5f, light: false, now: now);
        string hover = TaskbarHost.HoverText(codex with { Windows = new[] { first with { Used = 100 }, week } }, false, claude, now);
        check(hover.Contains("사용 불가") && hover.Contains("• 주간"), $"hover text marks the shown window and the unusable state ({hover.Replace("\n", " / ")})");
        check(TaskbarHost.HoverText(codex with { Windows = new[] { week } }, false, claude, now).Contains("주간 한도만 있음"), "hover text says this account has no 5-hour limit");
    }

    internal static void Run(string folder, Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        var week = new Quota("codex", "Codex", 63, 10080, now.AddDays(1));
        var first = new Quota("codex", "Codex", 37, 300, now.AddHours(2));
        var codex = new Snapshot(new[] { first, week }, "plus", null, true, null, now);
        var claude = new ClaudeReading(new[] { new Quota("claude", "Claude", 12, 300, now.AddMinutes(102)), new Quota("claude", "Claude", 54, 10080, now.AddDays(3)) }, now, "fixture");
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            string suffix = ((int)(scale * 100)).ToString();
            TaskbarHost.SavePreview(Path.Combine(folder, "rings-" + suffix + ".png"), codex, claude, false, scale, light: false, now: now);
        }
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-light.png"), codex, claude, false, 1.5f, light: true, now: now);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-narrow.png"), codex, claude, false, 1.5f, RingArtwork.MinimumWidth, false, now);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-no-claude.png"), codex, null, false, 1.5f, light: false, now: now);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-claude-stale.png"), codex, claude with { Updated = now.AddHours(-2) }, false, 1.5f, light: false, now: now);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-unknown.png"), null, null, false, 1.5f, light: false, now: now);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-denied.png"), codex with { Allowed = false }, claude, false, 1.5f, light: false, now: now);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-full-empty.png"), codex with { Windows = new[] { first with { Used = 0 }, week with { Used = 100 } } }, claude, false, 1.5f, light: false, now: now);

        TaskbarHost.SavePreview(Path.Combine(folder, "rings-hover.png"), codex, claude, false, 1.5f, light: false, now: now, pointer: true);
        TaskbarHost.SavePreview(Path.Combine(folder, "rings-light-hover.png"), codex, claude, false, 1.5f, light: true, now: now, pointer: true);
        var services = TaskbarHost.Services(codex, false, claude, now);
        using (var plain = RingArtwork.Render(RingArtwork.PreferredWidth, 44, 1, services, false, now))
        using (var hovered = RingArtwork.Render(RingArtwork.PreferredWidth, 44, 1, services, false, now, hover: true))
        {
            var corner = plain.GetPixel(RingArtwork.PreferredWidth - 2, 22);
            check(corner.A <= 1, "background stays transparent so the real taskbar material shows through");
            check(corner.A >= 1, "background keeps a hit-testable alpha for hover and clicks");
            check(hovered.GetPixel(RingArtwork.PreferredWidth - 3, 22).A > corner.A, "pointer hover adds a visible wash");
        }
        check(RingArtwork.Font(RingArtwork.NumberFont, 12).Name is RingArtwork.NumberFont or "Segoe UI", "ring numbers use Segoe UI Variable when installed, else Segoe UI");
        check(services[0].Center == "63" && services[1].Center == "88", "ring centers show the 5-hour value first, even when the weekly value is lower");
        check(services.All(s => s.Shown?.Minutes == 300 && !s.Blocked) && RingArtwork.Token(services[0], services[0].Shown!, now).Top == "5h", "each reset token names the window the center number shows");
        StateChecks(folder, now, week, first, codex, claude, check);
        var low = TaskbarHost.Services(codex with { Windows = new[] { first with { Used = 92 }, week } }, false, claude, now);
        using (var image = RingArtwork.Render(RingArtwork.PreferredWidth, 44, 1, low, false, now))
        {
            // The 5-hour ring is the lighter inner ring, so look for red-toned pixels (coral has G 128) rather than one exact value.
            int red = Enumerable.Range(0, 46).Sum(x => Enumerable.Range(0, 44).Count(y => image.GetPixel(x, y) is { A: > 100, R: > 180, G: < 100, B: < 100 }));
            check(red > 20, "a window under 20% turns its ring red");
        }
        using (var image = RingArtwork.Render(RingArtwork.PreferredWidth, 44, 1, services, false, now))
        {
            // Each service's slot: [ring][gap][token] then the service gap; Claude starts at the second slot.
            int slot = RingArtwork.Pad + RingArtwork.RingCell + RingArtwork.TokenGap + RingArtwork.TokenWidth + RingArtwork.ServiceGap;
            int purple = Enumerable.Range(slot, RingArtwork.PreferredWidth - slot).Sum(x => Enumerable.Range(0, 44).Count(y => image.GetPixel(x, y).ToArgb() == RingService.ClaudeAccent.ToArgb()));
            int coral = Enumerable.Range(0, slot).Sum(x => Enumerable.Range(0, 44).Count(y => image.GetPixel(x, y).ToArgb() == RingService.CodexAccent.ToArgb()));
            check(purple > 20 && coral > 20, "Codex and Claude rings use their own colors");
        }
        var unknown = TaskbarHost.Services(null, false, null, now);
        check(unknown.All(x => x.Center == "—"), "missing data renders a dash, never zero or full");
        var expired = claude with { Windows = new[] { new Quota("claude", "Claude", 70, 300, now.AddMinutes(-5)) } };
        check(TaskbarHost.Services(codex, false, expired, now)[1].FiveHour == null, "a Claude window past its reset is treated as unknown, not refilled");
        check(TaskbarHost.Services(codex, false, claude with { Updated = now.AddHours(-1) }, now)[1].Stale, "an old Claude report is marked stale");
        check(RingArtwork.ShortCountdown(first, now) is "2:00" or "1:59" && RingArtwork.ShortCountdown(week, now) == "1일" && RingArtwork.ShortCountdown(week with { Reset = now.AddMinutes(41.5) }, now) == "42분" && RingArtwork.ShortCountdown(week with { Reset = null }, now) == "—" && RingArtwork.ShortCountdown(week with { Reset = now.AddMinutes(-1) }, now) == "확인", "short countdown covers days, hours, minutes, missing and passed resets");
        // The reported clipping: every token must fit its reserved width at every scale.
        using (var probe = new System.Drawing.Bitmap(10, 10))
        using (var g = System.Drawing.Graphics.FromImage(probe))
        using (var top = new System.Drawing.Font("Malgun Gothic", 11, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
        using (var bottom = new System.Drawing.Font("Malgun Gothic", 12, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
        {
            var fmt = System.Drawing.StringFormat.GenericTypographic;
            float widest = new[] { "6일", "23:59", "59분", "확인", "—" }.Max(t => g.MeasureString(t, bottom, System.Drawing.PointF.Empty, fmt).Width);
            widest = Math.Max(widest, new[] { "5h", "주", RingArtwork.BlockedToken }.Max(t => g.MeasureString(t, top, System.Drawing.PointF.Empty, fmt).Width));
            check(widest <= RingArtwork.TokenWidth, $"longest reset token ({widest:0.#} DIP) fits beside its ring ({RingArtwork.TokenWidth} DIP)");
        }
        var area = new System.Drawing.Rectangle(0, 0, 2560, 72);
        var obstacles = new[] { new System.Drawing.Rectangle(0, 0, 237, 72), new System.Drawing.Rectangle(500, 0, 2060, 72) };
        var placement = TaskbarHost.FindMeterSpace(area, obstacles, 2140, 1.5f, 66);
        check(placement.HasValue && obstacles.All(r => !r.IntersectsWith(placement.Value)), "rings fit a narrow taskbar gap without covering buttons");
        check(TaskbarHost.FindMeterSpace(area, new[] { new System.Drawing.Rectangle(0, 0, 237, 72), new System.Drawing.Rectangle(330, 0, 2230, 72) }, 2140, 1.5f, 66) == null, "rings hide instead of overlapping when even two rings cannot fit");
    }
}

internal static class ClaudeVerification
{
    // Claude low-usage alerts reuse the Codex 20%/5% policy with a separate history file.
    private static void AlertChecks(string folder, DateTimeOffset now, Action<bool, string> check)
    {
        string dir = Path.Combine(folder, "claude-alerts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ClaudeReading R(DateTimeOffset updated, double fiveHourLeft, double weeklyLeft) => new(new[] {
            new Quota(ClaudeReading.Bucket, "Claude", 100 - fiveHourLeft, 300, now.AddHours(2)),
            new Quota(ClaudeReading.Bucket, "Claude", 100 - weeklyLeft, 10080, now.AddDays(3)) }, updated, "poll");
        UsageNotification? Observe(UsageAlerts p, ClaudeReading r) => p.Observe(r.AlertSnapshot(), now, true, 135);

        var claude = new UsageAlerts(Path.Combine(dir, "claude.json"), "Claude");
        check(Observe(claude, R(now, 25, 60)) == null, "Claude above 20 percent does not warn");
        var first = Observe(claude, R(now, 19, 60));
        check(first is { Critical: false, Warnings.Count: 1 } && first.Title == "Claude 잔여량 20% 미만" && first.Text.Contains("5시간 19%"),
            $"Claude below 20 percent warns once with a Claude title ({first?.Title})");
        check(Observe(claude, R(now, 18, 60)) == null && Observe(new UsageAlerts(Path.Combine(dir, "claude.json"), "Claude"), R(now, 17, 60)) == null,
            "repeated Claude readings and a restart do not repeat the warning");
        var critical = Observe(claude, R(now, 4, 60));
        check(critical is { Critical: true } && critical.Title == "Claude 잔여량 5% 미만", "Claude below 5 percent warns once more as critical");

        var stale = new UsageAlerts(Path.Combine(dir, "stale.json"), "Claude");
        check(Observe(stale, R(now.AddMinutes(-10), 3, 3)) == null && Observe(stale, R(now, 3, 3)) is { Warnings.Count: 2 },
            "an old Claude report (file left from earlier) neither warns nor consumes the first fresh warning");
        var expired = R(now, 2, 60) with { Windows = new[] { new Quota(ClaudeReading.Bucket, "Claude", 98, 300, now.AddMinutes(-1)) } };
        check(Observe(new UsageAlerts(Path.Combine(dir, "expired.json"), "Claude"), expired) == null,
            "a Claude window whose reset already passed does not warn on its old value");

        var codex = new UsageAlerts(Path.Combine(dir, "codex.json"));
        var codexLow = new Snapshot(new[] { new Quota("codex", "Codex", 81, 300, now.AddHours(2)) }, "pro", null, true, "account", now);
        var codexWarning = codex.Observe(codexLow, now, true, 135);
        var separate = new UsageAlerts(Path.Combine(dir, "separate.json"), "Claude");
        check(codexWarning?.Title == "Codex 잔여량 20% 미만" && Observe(separate, R(now, 19, 60)) != null,
            "Codex and Claude keep separate histories; one service's warning never silences the other");

        var muted = new UsageAlerts(Path.Combine(dir, "muted.json"), "Claude");
        check(muted.SetMuted(R(now, 30, 60).AlertSnapshot(), now, true) && muted.AnyMuted(R(now, 30, 60).AlertSnapshot()) &&
            Observe(muted, R(now, 3, 60)) == null, "pausing alerts until the next reset also silences Claude");
        check(ClaudeUsage.AlertsPath != UsageAlerts.DefaultPath && ClaudeUsage.AlertsPath.EndsWith("notifications-claude-v1.json"),
            "Claude alert history lives in its own file next to the Codex history");
    }

    internal static void Run(string folder, Action<bool, string> check)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
        var full = ClaudeUsage.ParseStatusLine("""{"session_id":"x","model":{"id":"m"},"rate_limits":{"five_hour":{"used_percentage":12.5,"resets_at":1800003600},"seven_day":{"used_percentage":54,"resets_at":1800300000}}}""", now);
        check(full != null && full.FiveHour?.Remaining == 87.5 && full.Weekly?.Remaining == 46 && full.FiveHour.Reset == DateTimeOffset.FromUnixTimeSeconds(1800003600), "statusLine rate_limits parsed into 5-hour and weekly windows");
        check(ClaudeUsage.ParseStatusLine("""{"session_id":"x"}""", now) == null, "missing rate_limits leaves the stored value untouched");
        var partial = ClaudeUsage.ParseStatusLine("""{"rate_limits":{"seven_day":{"used_percentage":130,"resets_at":"2027-01-01T00:00:00Z"}}}""", now);
        check(partial != null && partial.FiveHour == null && partial.Weekly?.Remaining == 0 && partial.Weekly.Reset?.Year == 2027, "absent windows stay absent; out-of-range values clamp; ISO reset accepted");
        var round = ClaudeUsage.Deserialize(ClaudeUsage.Serialize(full!));
        check(round != null && round.Updated == now && round.FiveHour?.Used == 12.5 && round.Weekly?.Reset == full!.Weekly!.Reset, "stored file round-trips");
        check(!ClaudeUsage.Serialize(full!).Contains("session", StringComparison.OrdinalIgnoreCase), "stored file keeps no session data");
        string path = Path.Combine(folder, "claude-usage-test.json");
        var output = new StringWriter();
        ClaudeUsage.RunStatusLine(new StringReader("""{"rate_limits":{"five_hour":{"used_percentage":40,"resets_at":1800003600}}}"""), output, path);
        var store = new ClaudeUsageStore(path);
        check(store.Refresh() && store.Current?.FiveHour?.Remaining == 60 && output.ToString().Contains("5h 60%"), "statusLine hook writes the file the meter reads and prints a short status");
        output = new StringWriter();
        ClaudeUsage.RunStatusLine(new StringReader("not json"), output, path);
        check(output.ToString().Length > 0 && ClaudeUsage.Deserialize(File.ReadAllText(path))?.FiveHour?.Remaining == 60, "malformed input never breaks Claude Code or erases the last value");
        AlertChecks(folder, now, check);
        // Shape observed from Claude Code 2.1.332 stream-json (utilization is a 0..1 fraction).
        var stream = new[]
        {
            """{"type":"system","subtype":"init"}""",
            "not json",
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1800003600,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":0.08,"resetsAt":1800003600,"status":"allowed"},"seven_day":{"utilization":0.14,"resetsAt":1800300000,"status":"allowed"}}}}""",
            """{"type":"result","subtype":"success"}""",
        };
        var polled = ClaudePoller.ParseStream(stream, now);
        check(polled != null && polled.FiveHour?.Remaining == 92 && polled.Weekly?.Remaining == 86 && polled.Weekly.Reset == DateTimeOffset.FromUnixTimeSeconds(1800300000) && polled.Source == "poll", "CLI rate_limit_event fractions become remaining percentages for both windows");
        var single = ClaudePoller.ParseStream(new[] { """{"type":"rate_limit_event","rate_limit_info":{"rateLimitType":"seven_day","utilization":0.5,"resetsAt":1800300000}}""" }, now);
        check(single != null && single.FiveHour == null && single.Weekly?.Remaining == 50, "older event shape without unifiedWindows still yields its one window");
        check(ClaudePoller.ParseStream(new[] { """{"type":"result"}""" }, now) == null, "no rate_limit_event means no reading, not zero usage");
    }
}
