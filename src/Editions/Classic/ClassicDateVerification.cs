using System;
using System.Drawing;
using System.IO;
using System.Linq;

namespace CodexMeter;

internal static class ClassicDateVerification
{
    internal static void Run(string folder, Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        var first = new Quota("codex", "Codex", 63, 300, now.AddHours(2));
        var week = new Quota("codex", "Codex", 73, 10080, now.AddDays(1));
        var dual = new Snapshot(new[] { first, week }, "plus", null, true, null, now);
        var single = dual with { Windows = new[] { week } };
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            var suffix = ((int)(scale * 100)).ToString();
            TaskbarHost.SavePreview(Path.Combine(folder, "classic-date-single-" + suffix + ".png"), single, false, scale);
            TaskbarHost.SavePreview(Path.Combine(folder, "classic-date-dual-" + suffix + ".png"), dual, false, scale);
        }
        TaskbarHost.SavePreview(Path.Combine(folder, "classic-date-full.png"), single with { Windows = new[] { week with { Used = 0 } } }, false, 1.5f);
        TaskbarHost.SavePreview(Path.Combine(folder, "classic-date-no-reset.png"), dual with { Windows = new[] { first with { Reset = null }, week with { Reset = null } } }, false, 1.5f);
        check(TaskbarHost.ResetText(null) == "시간 미제공" && TaskbarHost.ResetText(week) == week.Reset!.Value.ToLocalTime().ToString("MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture), "classic date suffix uses local numeric dates and preserves missing values");
        var obstacles = new[] { new Rectangle(0, 0, 237, 72), new Rectangle(650, 0, 1910, 72) };
        var position = TaskbarHost.FindMeterSpace(new Rectangle(0, 0, 2560, 72), obstacles, 2140, 1.5f, 54);
        check(position?.Width == 372 && obstacles.All(r => !r.IntersectsWith(position.Value)), "classic bar plus date fits reported taskbar gap at original font and bar sizes");
        using var rendered = new Bitmap(Path.Combine(folder, "classic-date-dual-150.png"));
        check(rendered.Size == new Size(372, 54), "classic date suffix increases width only, preserving v2.0 dual-row height");
        int datePixels = Enumerable.Range(246, 120).Sum(x => Enumerable.Range(0, 54).Count(y => rendered.GetPixel(x, y).R > 120));
        check(datePixels > 100, "classic date suffix is visibly painted after percentage");
    }
}
