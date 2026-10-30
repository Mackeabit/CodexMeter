using System;
using System.IO;
using System.Linq;

namespace CodexMeter;

internal static class DesignVerification
{
    internal static void Run(string folder, Action<bool, string> check)
    {
        var now = DateTimeOffset.Now;
        var week = new Quota("codex", "Codex", 73, 10080, now.AddDays(1));
        var first = new Quota("codex", "Codex", 63, 300, now.AddHours(2));
        var dual = new Snapshot(new[] { first, week }, "plus", null, true, null, now);
        var single = dual with { Windows = new[] { week }, Plan = "pro" };
        foreach (float scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            string suffix = ((int)(scale * 100)).ToString();
            TaskbarHost.SavePreview(Path.Combine(folder, "design-single-" + suffix + ".png"), single, false, scale, light: false);
            TaskbarHost.SavePreview(Path.Combine(folder, "design-dual-" + suffix + ".png"), dual, false, scale, light: false);
        }
        TaskbarHost.SavePreview(Path.Combine(folder, "design-light.png"), dual, false, 1.5f, light: true);
        TaskbarHost.SavePreview(Path.Combine(folder, "design-narrow.png"), dual, false, 1.5f, 240, false);
        TaskbarHost.SavePreview(Path.Combine(folder, "design-single-narrow.png"), single, false, 1.5f, 240, false);
        TaskbarHost.SavePreview(Path.Combine(folder, "design-minimum-full.png"), single with { Windows = new[] { week with { Used = 0 } } }, false, 1.5f, 240, false);
        TaskbarHost.SavePreview(Path.Combine(folder, "design-full.png"), single with { Windows = new[] { week with { Used = 0 } } }, false, 1.5f, light: false);
        TaskbarHost.SavePreview(Path.Combine(folder, "design-empty.png"), single with { Windows = new[] { week with { Used = 100 } } }, false, 1.5f, light: false);
        TaskbarHost.SavePreview(Path.Combine(folder, "design-unknown.png"), single with { Windows = Array.Empty<Quota>() }, false, 1.5f, light: false);
        TaskbarHost.SavePreview(Path.Combine(folder, "design-no-reset.png"), dual with { Windows = new[] { first with { Reset = null }, week with { Reset = null } } }, false, 1.5f, light: false);
        check(MeterArtwork.ResetParts(null).Time == "미제공" && MeterArtwork.ResetParts(week).Date == week.Reset!.Value.ToLocalTime().ToString("MM/dd", System.Globalization.CultureInfo.InvariantCulture), "redesigned meter uses numeric local dates and explicit missing reset state");
        using var singleImage = MeterArtwork.Render(336, 44, 1, new Quota?[] { week }, false, false, false);
        using var dualImage = MeterArtwork.Render(336, 44, 1, new Quota?[] { first, week }, false, false, false);
        int OrangeHeight(System.Drawing.Bitmap image, int start, int end) => Enumerable.Range(start, end - start).Count(y => image.GetPixel(6, y).ToArgb() == System.Drawing.Color.FromArgb(222, 128, 94).ToArgb());
        check(OrangeHeight(singleImage, 0, 44) > OrangeHeight(dualImage, 0, 22), "single quota renders a taller coral bar without inventing a second quota");
        // Reproduce the reported PC: 150% DPI, 237..650 px gap, 9 px side clearances.
        var area = new System.Drawing.Rectangle(0, 0, 2560, 72);
        var obstacles = new[] { new System.Drawing.Rectangle(0, 0, 237, 72), new System.Drawing.Rectangle(650, 0, 1910, 72) };
        var placement = TaskbarHost.FindMeterSpace(area, obstacles, 2140, 1.5f, 66);
        check(placement?.Width == 395 && obstacles.All(r => !r.IntersectsWith(placement.Value)), "reported 263-DIP gap fits the redesign without hiding reset dates or covering buttons");
        foreach (var pair in new[] { (Name: "single", Rows: new Quota?[] { week }), (Name: "dual", Rows: new Quota?[] { first, week }) })
        {
            using var regression = MeterArtwork.Render(placement!.Value.Width, 66, 1.5f, pair.Rows, false, false, false);
            regression.Save(Path.Combine(folder, "reported-gap-" + pair.Name + ".png"));
            int textPixels = Enumerable.Range(placement.Value.Width - 145, 140).Sum(x => Enumerable.Range(2, 62).Count(y => regression.GetPixel(x, y).R > 120));
            check(textPixels > 150, "reset text is actually painted at reported gap width: " + pair.Name);
        }
        check(TaskbarHost.FindMeterSpace(area, new[] { new System.Drawing.Rectangle(0, 0, 237, 72), new System.Drawing.Rectangle(590, 0, 1970, 72) }, 2140, 1.5f, 66) == null, "layout never silently drops reset information below its minimum width");
    }
}
