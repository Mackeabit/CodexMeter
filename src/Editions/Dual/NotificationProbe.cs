using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Forms = System.Windows.Forms;

namespace CodexMeter;

/// <summary>
/// <c>CodexMeter.exe --verify-notification-display &lt;folder&gt; [gapSeconds]</c>: shows a Codex and then a Claude
/// low-usage notification through a temporary tray icon, the same ShowBalloonTip path the app uses, so a person can
/// see how Windows presents two notifications arriving close together (and under Focus Assist). Uses synthetic
/// values with in-memory alert policies: no account requests, settings or alert history are touched.
/// </summary>
internal static class NotificationProbe
{
    public static int Run(string[] args)
    {
        var values = args.SkipWhile(x => x != "--verify-notification-display").Skip(1).ToArray();
        string folder = Path.GetFullPath(values[0]);
        double gap = values.Length > 1 && double.TryParse(values[1], out var g) ? g : 2;
        Directory.CreateDirectory(folder);
        var now = DateTimeOffset.Now;
        var codex = new UsageAlerts(null).Observe(new Snapshot(new[] { new Quota("codex", "Codex", 85, 10080, now.AddDays(6)) }, "probe", null, true, null, now), now, true);
        var claude = new UsageAlerts(null, "Claude").Observe(new ClaudeReading(new[] { new Quota(ClaudeReading.Bucket, "Claude", 97, 300, now.AddHours(2)) }, now, "probe").AlertSnapshot(), now, true);
        string? failure = null;
        using var icon = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Codex Meter 알림 확인", Visible = true };
        try
        {
            Show(icon, codex!);
            Pump(TimeSpan.FromSeconds(gap));
            Show(icon, claude!);
            Pump(TimeSpan.FromSeconds(8));
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { failure = e.GetType().Name; }
        finally { icon.Visible = false; }
        File.WriteAllText(Path.Combine(folder, "notification-display.json"), JsonSerializer.Serialize(new {
            passed = failure == null && codex != null && claude != null, version = Installation.Version, gapSeconds = gap,
            windowsAcceptsNotifications = NoticeQueue.WindowsAcceptsNotifications(),
            codex = new { codex?.Title, codex?.Text, codex?.Critical }, claude = new { claude?.Title, claude?.Text, claude?.Critical }, failure,
            note = "Delivery and presentation are decided by Windows notification settings; check the screen or notification center."
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return failure == null && codex != null && claude != null ? 0 : 1;
    }

    private static void Show(Forms.NotifyIcon icon, UsageNotification n) =>
        icon.ShowBalloonTip(5000, n.Title, n.Text, n.Critical ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);

    // The tray icon's hidden window needs messages pumped while the notification is up.
    private static void Pump(TimeSpan span)
    {
        var until = DateTime.UtcNow + span;
        while (DateTime.UtcNow < until) { Forms.Application.DoEvents(); Thread.Sleep(50); }
    }
}
