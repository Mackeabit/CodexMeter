using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CodexMeter;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--install")) return Installation.Run();
        if (args.Contains("--verify-alerts")) return AlertVerification.Run(args);
        if (args.Contains("--verify-alert-probe")) return AlertVerification.Probe(args);
        if (args.Contains("--verify-portability")) return PortabilityVerification.Run(args);
        if (args.Contains("--verify-taskbar-layout")) return TaskbarLayoutVerification.Run(args);
        if (args.Contains("--diagnose"))
        {
            string output = args.SkipWhile(x => x != "--diagnose").Skip(1).First();
            string? codex = null, error = null;
            try { codex = CodexLocator.ResolveAsync(null).GetAwaiter().GetResult(); } catch (Exception e) { error = e.GetType().Name; }
            File.WriteAllText(output, JsonSerializer.Serialize(new {
                version = Installation.Version,
                application = Environment.ProcessPath,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                runtimeDirectory = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
                codex, discoveryError = error
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Contains("--capture-taskbar"))
        {
            TaskbarNative.CaptureTaskbar(args.SkipWhile(x => x != "--capture-taskbar").Skip(1).First());
            return 0;
        }
        if (args.Contains("--inspect-taskbar"))
        {
            string output = args.SkipWhile(x => x != "--inspect-taskbar").Skip(1).First();
            File.WriteAllText(output, JsonSerializer.Serialize(TaskbarNative.Inspect(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Contains("--probe"))
        {
            string output = args.SkipWhile(x => x != "--probe").Skip(1).First();
            try
            {
                var s = new UsageClient().ReadAsync().GetAwaiter().GetResult();
                File.WriteAllText(output, JsonSerializer.Serialize(new { ok = true, fetched = s.Fetched, plan = s.Plan, ordinaryUsageAllowed = s.Allowed, resetCredits = s.Resets, windows = s.Windows.Select(w => new { bucket = w.Bucket, minutes = w.Minutes, remaining = w.Remaining, reset = w.Reset }) }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception e) { File.WriteAllText(output, JsonSerializer.Serialize(new { ok = false, errorType = e.GetType().Name })); return 1; }
        }
        if (args.Contains("--verify") || args.Contains("--verify-live")) return Verification.Run(args);
        // Double-click runs in place. The separate Setup EXE owns installation.
        using var mutex = new Mutex(true, "Local\\CodexMeter.v1", out bool first);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexMeter.Show.v1");
        using var taskbarOn = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexMeter.TaskbarOn.v1");
        using var taskbarOff = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexMeter.TaskbarOff.v1");
        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexMeter.Exit.v2");
        if (!first && args.Contains("--startup")) return 0;
        if (!first) { (args.Contains("--taskbar-on") ? taskbarOn : args.Contains("--taskbar-off") ? taskbarOff : showSignal).Set(); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow();
        if (args.Contains("--taskbar-on") || args.Contains("--taskbar-off")) window.Loaded += (_, _) => window.SetTaskbar(args.Contains("--taskbar-on"));
        app.MainWindow = window;
        using var done = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            while (!done.IsCancellationRequested)
            {
                int signal = WaitHandle.WaitAny(new WaitHandle[] { showSignal, taskbarOn, taskbarOff, exitSignal }, 500);
                if (done.IsCancellationRequested) break;
                if (signal == 0) app.Dispatcher.BeginInvoke(window.ShowWidget);
                if (signal == 1) app.Dispatcher.BeginInvoke(() => window.SetTaskbar(true));
                if (signal == 2) app.Dispatcher.BeginInvoke(() => window.SetTaskbar(false));
                if (signal == 3) app.Dispatcher.BeginInvoke(window.Exit);
            }
        }) { IsBackground = true };
        thread.Start();
        app.DispatcherUnhandledException += (_, e) =>
        {
            // Never write exception messages or server payloads to disk.
            try { var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMeter"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "last-error.txt"), DateTimeOffset.Now.ToString("O") + " " + e.Exception.GetType().Name); } catch { }
            e.Handled = true;
            System.Windows.MessageBox.Show("화면 처리 중 문제가 발생했습니다. Codex Meter를 다시 실행해 주세요.", "Codex Meter");
            window.Exit();
        };
        app.Run(window);
        done.Cancel(); thread.Join(1000);
        return 0;
    }
}

public static class Verification
{
    public static int Run(string[] args)
    {
        bool live = args.Contains("--verify-live");
        string flag = live ? "--verify-live" : "--verify";
        string folder = Path.GetFullPath(args.SkipWhile(x => x != flag).Skip(1).First());
        Directory.CreateDirectory(folder);
        var checks = new List<string>();
        void Check(bool pass, string name) { if (!pass) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var week = Snapshot.Parse("""{"rateLimits":{"primary":{"usedPercent":73,"windowDurationMins":10080,"resetsAt":1791428417},"secondary":null,"planType":"pro"},"ordinaryUsageAllowed":true,"rateLimitResetCredits":{"availableCount":1}}""");
            Check(week.Main?.Remaining == 27 && week.Main.Duration == "주간" && week.Windows.Count == 1, "weekly-only slot is correctly labelled");
            var dual = Snapshot.Parse("""{"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":45,"windowDurationMins":300},"secondary":{"usedPercent":88,"windowDurationMins":10080}},"other":{"primary":{"usedPercent":99,"windowDurationMins":60}}},"rateLimits":{"primary":{"usedPercent":10}}} """);
            Check(dual.Windows.Count == 3 && dual.Main?.Remaining == 12 && dual.Main.Bucket == "codex", "multi-bucket preferred; Codex limiting window selected without duplication");
            var unknown = Snapshot.Parse("""{"rateLimits":{"primary":{"usedPercent":null,"windowDurationMins":null},"secondary":null},"rateLimitResetCredits":{"availableCount":null},"ordinaryUsageAllowed":null}""");
            Check(unknown.Main == null && unknown.Resets == null && unknown.Allowed == null, "missing values stay unknown, never become zero or unlimited");
            var bounds = Snapshot.Parse("""{"rateLimits":{"primary":{"usedPercent":135,"windowDurationMins":null,"resetsAt":9223372036854775807},"secondary":{"usedPercent":-5}}}""");
            Check(bounds.Windows[0].Remaining == 0 && bounds.Windows[1].Remaining == 100 && bounds.Windows[0].Reset == null, "out-of-range percentages and timestamps handled");
            var old = new Quota("codex", "Codex", 73, 10080, DateTimeOffset.Now.AddMinutes(-1));
            Check(old.ResetPending(DateTimeOffset.Now) && old.Remaining == 27 && old.Countdown(DateTimeOffset.Now).Contains("확인"), "past reset never manufactures a refill");
            Check(Snapshot.Parse("""{"ordinaryUsageAllowed":false,"rateLimits":{"primary":{"usedPercent":0}}}""").Allowed == false, "server denial preserved even at 100 percent remaining");
            var area = new System.Drawing.Rectangle(0, 0, 1200, 48);
            var blocked = new[] { new System.Drawing.Rectangle(0, 0, 70, 48), new System.Drawing.Rectangle(320, 0, 700, 48) };
            var space = TaskbarHost.FindSpace(area, blocked, 1100, 158, 24, 6);
            Check(space.HasValue && space.Value.Right < 320 && blocked.All(r => !r.IntersectsWith(space.Value)), "taskbar placement finds a real gap without covering app buttons");
            Check(TaskbarHost.FindSpace(area, new[] { area }, 1100, 158, 24, 6) == null, "full taskbar hides the meter instead of covering controls");
            Check(TaskbarHost.FindSpace(area, null, 1100, 158, 24, 6) == null, "unknown taskbar layout does not guess a position");
            Check(TaskbarHost.FindSpace(new System.Drawing.Rectangle(0, 0, 1200, 10), Array.Empty<System.Drawing.Rectangle>(), 1100, 158, 24, 6) == null, "meter respects taskbar height");
            var narrowArea = new System.Drawing.Rectangle(0, 0, 2560, 72);
            var narrowObstacles = new[] { new System.Drawing.Rectangle(15, 0, 66, 72), new System.Drawing.Rectangle(291, 0, 2269, 72) };
            var responsive = TaskbarHost.FindResponsiveSpace(narrowArea, narrowObstacles, 2100, 237, 177, 36, 9);
            Check(responsive?.Width == 192 && narrowObstacles.All(r => !r.IntersectsWith(responsive.Value)), "taskbar shrinks the bar to the observed narrow gap instead of disappearing");
            Check(TaskbarHost.FindResponsiveSpace(narrowArea, new[] { new System.Drawing.Rectangle(180, 0, 2380, 72) }, 2100, 237, 177, 36, 9) == null, "adaptive bar still hides when even its minimum width cannot fit");
            UsageHoverVerification.Run(Check);
            DesignVerification.Run(folder, Check);
            if (live) week = new UsageClient().ReadAsync().GetAwaiter().GetResult();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow(true) { ShowActivated = false };
            string? uiError = null;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var connection = new ConnectionWindow(new UsageClient(), _ => { }) { ShowActivated = false };
                    connection.Show();
                    await connection.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    var connectionImage = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(connection.ActualWidth * 2), (int)(connection.ActualHeight * 2), 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
                    connectionImage.Render(connection);
                    var connectionEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    connectionEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(connectionImage));
                    using (var png = File.Create(Path.Combine(folder, "connection-settings.png"))) connectionEncoder.Save(png);
                    Check(connection.ActualWidth > 0 && connection.ActualHeight > 0, "connection and startup settings window renders");
                    connection.Close();
                    window.SetSnapshot(week);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.Capture(Path.Combine(folder, live ? "live-expanded.png" : "expanded.png"));
                    Check(window.AmountText.Text == week.Main?.Remaining.ToString("0.#"), "rendered percentage matches parsed snapshot");
                    window.SetView(WindowView.Compact, false);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.Capture(Path.Combine(folder, live ? "live-compact.png" : "compact.png"));
                    TrayArtwork.SavePreview(Path.Combine(folder, "tray-sizes.png"));
                    Check(window.Compact.Visibility == Visibility.Visible && window.Expanded.Visibility == Visibility.Collapsed, "compact view switches correctly");
                    window.SetView(WindowView.Detail, false);
                    window.SetSnapshot(week, "테스트: 네트워크 연결 실패 · 마지막 확인값을 표시합니다.");
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.Capture(Path.Combine(folder, "stale.png"));
                    Check(window.StateText.Text == "갱신 실패" && window.ErrorPanel.Visibility == Visibility.Visible, "failed refresh is prominently marked stale");
                    window.SetSnapshot(unknown);
                    Check(window.AmountText.Text == "—" && window.CreditText.Text == "—", "unknown UI uses dashes");
                    window.SetSnapshot(dual);
                    Check(window.ExtraWindows.Children.Count == 2, "additional buckets remain visible");
                    window.SetSnapshot(new Snapshot(new[] { old }, "pro", 1, true, null, DateTimeOffset.Now));
                    Check(window.StateText.Text == "확인 중", "expired reset shown as awaiting server confirmation");
                    var bars = new Snapshot(new[] {
                        new Quota("codex", "Codex", 63, 300, DateTimeOffset.Now.AddHours(2)),
                        new Quota("codex", "Codex", 80, 10080, DateTimeOffset.Now.AddDays(5))
                    }, "pro", 1, true, null, DateTimeOffset.Now);
                    TaskbarHost.SavePreview(Path.Combine(folder, "taskbar-150.png"), bars, false, 1.5f);
                    TaskbarHost.SavePreview(Path.Combine(folder, "taskbar-stale-150.png"), bars, true, 1.5f);
                    string BarText(int row) => string.Concat(((System.Windows.Controls.Grid)window.SegmentRows.Children[row]).Children.OfType<System.Windows.Controls.TextBlock>().Single(n => (string?)n.Tag == "remaining").Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
                    window.SetView(WindowView.Segmented, false);
                    window.SetSnapshot(bars);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.Capture(Path.Combine(folder, "segmented.png"));
                    Check(window.SegmentRows.Visibility == Visibility.Visible && window.Expanded.Visibility == Visibility.Collapsed && window.SegmentRows.Children.Count == 2 && BarText(0) == "37%" && BarText(1) == "20%", $"segmented view shows each real limit with remaining percentage ({BarText(0)} / {BarText(1)})");
                    window.SetSnapshot(bars, "test failure");
                    Check(BarText(0) == "!" && BarText(1) == "!", "segmented stale values are visibly flagged");
                    window.Capture(Path.Combine(folder, "segmented-stale.png"));
                    window.SetSnapshot(unknown);
                    Check(window.SegmentRows.Children.Count == 1 && BarText(0) == "—", "segmented unknown stays unknown and creates no fake second limit");
                    window.SetSnapshot(bars with { Allowed = false });
                    Check(BarText(0) == "!", "segmented server denial remains visible");
                    window.SetSnapshot(week);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    window.Capture(Path.Combine(folder, live ? "live-segmented.png" : "segmented-single.png"));
                    window.SetView(WindowView.Detail, false);
                    Check(window.SegmentRows.Visibility == Visibility.Collapsed && window.Expanded.Visibility == Visibility.Visible && window.Width == 348, "segmented view returns to original detail layout");
                    SettingsVerification.Run(window, Check);
                    window.Hide(); Check(!window.IsVisible, "hide works"); window.ShowWidget(); Check(window.IsVisible, "restore works");
                }
                catch (Exception e) { uiError = e.Message; }
                finally { window.Exit(); app.Shutdown(); }
            }, DispatcherPriority.ApplicationIdle);
            app.Run(window);
            if (uiError != null) throw new InvalidOperationException(uiError);
            File.WriteAllText(Path.Combine(folder, "verification.json"), JsonSerializer.Serialize(new { passed = true, mode = live ? "live-account-and-ui-render" : "synthetic-parser-and-ui", checkedAt = DateTimeOffset.Now, checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(folder, "verification.json"), JsonSerializer.Serialize(new { passed = false, checks, error = e.Message }));
            return 1;
        }
    }
}
