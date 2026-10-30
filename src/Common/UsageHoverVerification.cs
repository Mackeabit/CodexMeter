using System;
using Forms = System.Windows.Forms;

namespace CodexMeter;

internal static class UsageHoverVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var now = new DateTimeOffset(2026, 10, 7, 15, 50, 0, TimeSpan.FromHours(9));
        var first = new Quota("codex", "Codex", 63, 300, now.AddHours(2));
        var second = new Quota("codex", "Codex", 73, 10080, now.AddDays(1));
        var snapshot = new Snapshot(new[] { second, first }, "plus", null, true, null, now);
        var text = UsageHover.Text(snapshot, false, now);
        check(text.Contains("37%") && text.Contains("27%") && text.IndexOf("37%") < text.IndexOf("27%"), "hover shows both quotas in short-window then weekly order");
        check(text.Contains(first.Reset!.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)) && text.Contains(second.Reset!.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)) && text.Contains("2시간"), "hover includes each local reset date, time and countdown");
        check(UsageHover.Rows(snapshot with { Plan = "pro" }).Length == 2 && UsageHover.Rows(snapshot with { Windows = new[] { second }, Plan = "plus" }).Length == 1, "row count follows reported limits, never the plan name");
        check(UsageHover.Text(null, false, now).Contains("미확인") && UsageHover.Describe(first with { Reset = null }, false, false, now).Contains("미제공"), "unknown quotas and reset times are not invented");
        check(UsageHover.Text(snapshot, true, now).Contains("마지막 확인값") && UsageHover.Text(snapshot with { Allowed = false }, false, now).Contains("사용 제한"), "hover clearly marks stale and denied data");
        check(UsageHover.Describe(first with { Reset = now.AddMinutes(-1) }, false, false, now).Contains("반영 확인 중"), "expired resets await server confirmation");
        using var owner = new Forms.Form { ShowInTaskbar = false };
        IntPtr tipHandle;
        using (var tip = new UsageToolTip(owner.Handle))
        {
            tipHandle = tip.Handle;
            check(tip.Registered, "native hover tool registers against our own window");
            tip.Update(text);
            check(tip.RegisteredText == text, "native hover stores complete multiline reset content");
            tip.Update(UsageHover.Text(snapshot with { Windows = new[] { second } }, false, now));
            check(!tip.RegisteredText.Contains("37%") && tip.RegisteredText.Contains("27%"), "hover updates when account quota count changes");
            tip.Hide();
        }
        check(!TaskbarNative.IsWindow(tipHandle), "native tooltip is released with its owner");
    }
}
