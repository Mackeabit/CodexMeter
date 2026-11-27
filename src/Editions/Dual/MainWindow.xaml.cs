using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexMeter;

public partial class MainWindow : Window
{
    private readonly bool testing;
    private readonly UsageClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private Forms.NotifyIcon? tray;
    private TaskbarHost? taskbarHost;
    private Drawing.Icon? currentIcon;
    private Snapshot? snapshot;
    private ClaudeReading? claude;
    private readonly ClaudeUsageStore claudeStore = new();
    private readonly ClaudePoller claudePoller = new();
    private bool claudeBusy;
    private DateTimeOffset lastClaudeAttempt = DateTimeOffset.MinValue;
    private bool busy, exiting;
    private string? trayState;
    private string? segmentedState;
    private string? error;
    private DateTimeOffset nextPoll = DateTimeOffset.MinValue;
    private readonly UsageAlerts alerts;
    private readonly UsageAlerts claudeAlerts;
    private Preferences prefs = new();
    private readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMeter", "settings.json");
    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    public MainWindow(bool testing = false)
    {
        this.testing = testing;
        alerts = new UsageAlerts(testing ? null : UsageAlerts.DefaultPath);
        claudeAlerts = new UsageAlerts(testing ? null : ClaudeUsage.AlertsPath, "Claude");
        InitializeComponent();
        if (!testing) try { if (File.Exists(settingsFile)) prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(settingsFile)) ?? new(); } catch { }
        // Dual refreshes Codex and Claude together; a first run starts at one minute. Saved choices are kept.
        if (testing || !File.Exists(settingsFile)) prefs.IntervalSeconds = 60;
        prefs.Normalize();
        Topmost = prefs.Topmost; Opacity = prefs.Opacity;
        client.PreferredPath = prefs.CodexPath;
        BuildDetailView();
        ApplyView(false);
        Loaded += async (_, _) =>
        {
            PlaceInitial(); KeepVisible();
            if (!testing) { claudeStore.Refresh(); claude = claudeStore.Current; CreateTray(); CheckClaudeAlert(); if (prefs.Taskbar) { SetTaskbar(true, false); Hide(); } clock.Start(); await RefreshAsync(); }
        };
        clock.Tick += async (_, _) => { if (!testing && claudeStore.Refresh()) { claude = claudeStore.Current; CheckClaudeAlert(); } RenderState(); UpdateTray(); UpdateTaskbar(); if (!busy && DateTimeOffset.Now >= nextPoll) await RefreshAsync(); if (!testing) await PollClaudeAsync(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); if (e.Key == Key.F5) _ = RefreshAsync(); };
        MouseRightButtonUp += (_, _) => ShowSettings();
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { lifetime.Cancel(); clock.Stop(); taskbarHost?.Dispose(); tray?.Dispose(); currentIcon?.Dispose(); if (!testing) Save(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
    }
    private void PlaceInitial()
    {
        if (prefs.Left is double l && prefs.Top is double t && double.IsFinite(l) && double.IsFinite(t))
        {
            Left = l; Top = t;
            var rect = new Drawing.Rectangle((int)(l * VisualTreeHelper.GetDpi(this).DpiScaleX), (int)(t * VisualTreeHelper.GetDpi(this).DpiScaleY), 100, 50);
            if (!Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) PlaceDefault();
        }
        else PlaceDefault();
    }
    private void PlaceDefault()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = Math.Max(area.Top, area.Bottom - ActualHeight - 12);
    }
    private void KeepVisible()
    {
        var source = System.Windows.PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var area = Forms.Screen.FromHandle(handle).WorkingArea;
        var transform = source.CompositionTarget.TransformFromDevice;
        var a = transform.Transform(new Point(area.Left, area.Top));
        var b = transform.Transform(new Point(area.Right, area.Bottom));
        Left = Math.Clamp(Left, a.X, Math.Max(a.X, b.X - ActualWidth));
        Top = Math.Clamp(Top, a.Y, Math.Max(a.Y, b.Y - ActualHeight));
    }
    private TimeSpan StaleAfter => TimeSpan.FromSeconds(Math.Max(180, prefs.IntervalSeconds * 2 + 15));
    private bool IsStale(DateTimeOffset now) => error != null || snapshot?.Main?.ResetPending(now) == true || (snapshot != null && now - snapshot.Fetched > StaleAfter);
    // Claude follows the same refresh interval, so it goes stale on the same schedule as Codex.
    private bool ClaudeStale(DateTimeOffset now) => claude != null && now - claude.Updated > StaleAfter;
    public void ShowWidget() { Show(); WindowState = WindowState.Normal; }
    private void Save()
    {
        if (testing) return;
        prefs.Left = Left; prefs.Top = Top;
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); var temp = settingsFile + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(prefs)); File.Move(temp, settingsFile, true); } catch { }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    private void UpdateTray()
    {
        if (tray == null) return;
        var now = DateTimeOffset.Now;
        bool stale = IsStale(now);
        var services = TaskbarHost.Services(snapshot, stale, claude, now, ClaudeStale(now));
        var fresh = services.Where(x => !x.Warning && !x.Stale && x.Lowest != null).Select(x => x.Lowest!).OrderBy(x => x.Remaining).FirstOrDefault();
        var any = services.Where(x => !x.Warning && x.Lowest != null).Select(x => x.Lowest!).OrderBy(x => x.Remaining).FirstOrDefault();
        var main = fresh ?? any;
        string text = main == null ? (stale ? "!" : "—") : fresh == null ? "!" : Math.Floor(main.Remaining).ToString();
        bool light = TrayArtwork.IsLightTaskbar();
        int iconSize = Math.Clamp(Forms.SystemInformation.SmallIconSize.Width, 16, 64);
        var state = $"{text}:{stale}:{main?.Bucket}:{main?.Duration}:{snapshot?.Allowed}:{light}:{iconSize}:{main?.Remaining}:{claude?.Updated}";
        if (state == trayState) return;
        trayState = state;
        using var bitmap = TrayArtwork.Render(text, iconSize, light);
        var h = bitmap.GetHicon();
        using var borrowed = Drawing.Icon.FromHandle(h);
        var icon = (Drawing.Icon)borrowed.Clone();
        DestroyIcon(h);
        tray.Icon = icon;
        currentIcon?.Dispose(); currentIcon = icon;
        string Part(RingService x) => x.Lowest is Quota q ? $"{x.Short} {q.Remaining:0}%" + (x.Stale ? "(이전 값)" : "") : $"{x.Short} 미확인";
        var label = "Codex Meter · " + string.Join(" · ", services.Select(Part));
        tray.Text = label.Length > 127 ? label[..127] : label;
    }
    public async Task RefreshAsync()
    {
        if (busy || testing || exiting) return;
        busy = true; RefreshButton.IsEnabled = false; SetDetailRefreshing(true);
        if (snapshot == null) StateText.Text = "연결 중";
        try
        {
            var received = await client.ReadAsync(lifetime.Token);
            if (exiting) return;
            snapshot = received; error = null;
            nextPoll = DateTimeOffset.Now.AddSeconds(prefs.IntervalSeconds);
            CheckAlert();
        }
        catch (OperationCanceledException) { if (!exiting) error = "응답 시간이 초과되었습니다. 잠시 후 자동으로 다시 확인합니다."; }
        catch (FileNotFoundException e) { error = e.Message; }
        catch (Exception) { error = "조회에 실패했습니다. Codex 로그인과 네트워크를 확인하세요. 이전 값은 최신 사용량이 아닙니다."; }
        finally
        {
            busy = false;
            if (error != null) nextPoll = DateTimeOffset.Now.AddMinutes(5);
            RefreshButton.IsEnabled = true; SetDetailRefreshing(claudeBusy); RenderState(); UpdateTray(); UpdateTaskbar();
        }
    }
    private void CheckAlert() => Notify(alerts.Observe(snapshot, DateTimeOffset.Now, prefs.Alerts && tray != null, StaleAfter.TotalSeconds));
    // Claude follows the same 20%/5% policy with its own history; readings older than one stale period
    // (an old file at startup, a statusLine report from long ago) never raise a warning.
    private void CheckClaudeAlert() => Notify(claudeAlerts.Observe(ClaudeAlertSnapshot, DateTimeOffset.Now, prefs.Alerts && tray != null, StaleAfter.TotalSeconds));
    private Snapshot? ClaudeAlertSnapshot => claude?.AlertSnapshot();
    private void Notify(UsageNotification? notification)
    {
        if (notification == null || testing) return;
        // OS notification settings control delivery. Do not retry a delivered policy event.
        try { tray?.ShowBalloonTip(5000, notification.Title, notification.Text,
            notification.Critical ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private void ToggleAlerts()
    {
        prefs.Alerts = !prefs.Alerts;
        if (!testing) Save();
        nextPoll = DateTimeOffset.MinValue;
        CheckClaudeAlert();
    }
    // One pause covers both services: it stays until each service's own next reset.
    private bool AlertsMuted => alerts.AnyMuted(snapshot) || claudeAlerts.AnyMuted(ClaudeAlertSnapshot);
    private string AlertPauseTitle => AlertsMuted ? "알림 다시 켜기" : "다음 리셋까지 알림 끄기";
    private bool AlertQuotasKnown => alerts.HasQuotas(snapshot) || claudeAlerts.HasQuotas(ClaudeAlertSnapshot);
    private string? AlertStorageWarning => alerts.StorageWarning ?? claudeAlerts.StorageWarning;
    private void ToggleAlertPause()
    {
        bool mute = !AlertsMuted;
        var now = DateTimeOffset.Now;
        alerts.SetMuted(snapshot, now, mute);
        claudeAlerts.SetMuted(ClaudeAlertSnapshot, now, mute);
        nextPoll = DateTimeOffset.MinValue;
    }
    public void SetSnapshot(Snapshot? value, string? failure = null) { snapshot = value; error = failure; RenderState(); }
    public void SetClaude(ClaudeReading? value) { claude = value; segmentedState = null; RenderState(); }
    public void RenderState()
    {
        var now = DateTimeOffset.Now;
        var q = snapshot?.Main;
        bool old = IsStale(now);
        bool denied = snapshot?.Allowed == false;
        var accent = Brush(old ? "#777777" : "#181818");
        AmountText.Text = q == null ? "—" : q.Remaining.ToString("0.#");
        SmallAmount.Text = q == null ? "—%" : $"{q.Remaining:0.#}%";
        AmountText.Foreground = accent;
        SmallAmount.Foreground = accent;
        ProgressFill.Background = accent;
        RemainingLabel.Visibility = AmountText.Text.Length > 3 ? Visibility.Collapsed : Visibility.Visible;
        QuotaTitle.Text = q == null ? "사용량 정보 대기 중" : $"{q.Duration} 남은 사용량";
        StateText.Text = error != null ? "갱신 실패" : q?.ResetPending(now) == true ? "확인 중" : old ? "갱신 대기" : denied ? "사용 제한" : snapshot == null ? "연결 중" : q == null ? "미제공" : "동기화됨";
        StateDot.Fill = accent;
        CaptionText.Text = q == null ? "조회 가능한 한도 정보가 없습니다" : old ? "마지막 확인값 · 새 조회 대기 중" : denied ? "Codex에서 사용 제한 상태를 확인하세요" : $"{q.Used:0.#}% 사용 · Codex / Work 공통";
        PlanText.Text = (snapshot?.Plan ?? "CODEX").ToUpperInvariant();
        CreditText.Text = snapshot?.Resets is int count ? $"{count}회" : "—";
        ResetText.Text = q?.Reset?.ToLocalTime().ToString("M월 d일  HH:mm") ?? "시간 미제공";
        CountdownText.Text = q?.Countdown(now) ?? "조회 후 표시됩니다";
        SmallLabel.Text = old ? "갱신 확인 필요" : denied ? "사용 제한" : q == null ? "확인 중" : $"{q.Duration} 남음";
        SmallDetail.Text = q == null ? "Codex · ChatGPT Work" : old ? "이전 값 · 상세에서 상태 확인" : q.Countdown(now);
        UpdatedText.Text = snapshot == null ? "아직 조회하지 않았습니다" : $"{snapshot.Fetched.ToLocalTime():HH:mm} 확인 · {prefs.IntervalSeconds / 60}분마다 갱신";
        UpdateProgress();
        ErrorPanel.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Text = error ?? "";
        ExtraWindows.Children.Clear();
        foreach (var w in (snapshot?.Windows.Where(x => !ReferenceEquals(x, q)) ?? Enumerable.Empty<Quota>()).Concat(claude?.Known(now) ?? Array.Empty<Quota>()))
        {
            var row = new Grid { Margin = new Thickness(2, 5, 2, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = (w.Bucket == "codex" ? "" : w.Label + " · ") + w.Duration, FontSize = 12, Foreground = Brush("#666666"), TextTrimming = TextTrimming.CharacterEllipsis };
            var amount = new TextBlock { Text = $"{w.Remaining:0.#}% 남음", FontSize = 12, Foreground = Brush("#181818"), Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(amount, 1); row.Children.Add(label); row.Children.Add(amount);
            row.ToolTip = $"{w.Countdown(now)} · {w.Reset?.ToLocalTime():M/d HH:mm}" + (w.Bucket == ClaudeReading.Bucket && claude != null ? $" · {claude.Updated.ToLocalTime():HH:mm} 기준 값" : "");
            ExtraWindows.Children.Add(row);
        }
        ExtraWindows.Visibility = ExtraWindows.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderDetail(now);
        RenderSegments();
    }
    private void ApplyView(bool save)
    {
        double oldHeight = ActualHeight;
        bool bars = prefs.Segmented;
        // The shared Codex-first detail layout stays hidden; Dual shows both services with equal weight.
        Expanded.Visibility = Visibility.Collapsed;
        if (DetailPanel != null) DetailPanel.Visibility = !bars && !prefs.Compact ? Visibility.Visible : Visibility.Collapsed;
        Compact.Visibility = !bars && prefs.Compact ? Visibility.Visible : Visibility.Collapsed;
        SegmentRows.Visibility = bars ? Visibility.Visible : Visibility.Collapsed;
        Outer.Margin = new Thickness(bars ? 3 : 10);
        Outer.CornerRadius = new CornerRadius(bars ? 5 : 13);
        Outer.Background = Brush(bars ? "#252525" : "#FFFFFF");
        Outer.BorderBrush = Brush(bars ? "#3D3D3D" : "#D5D5D5");
        Outer.Effect = bars ? null : new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Opacity = .14 };
        Width = bars ? 360 : prefs.Compact ? 330 : 348;
        RenderSegments();
        if (IsLoaded) { UpdateLayout(); Top = Math.Max(SystemParameters.VirtualScreenTop, Top + oldHeight - ActualHeight); KeepVisible(); }
        if (save && !testing) Save();
    }
    private void RenderSegments()
    {
        if (!prefs.Segmented) return;
        var now = DateTimeOffset.Now;
        var windows = (snapshot?.Windows ?? Array.Empty<Quota>()).Concat(claude?.Known(now) ?? Array.Empty<Quota>()).OrderBy(q => q.Bucket == ClaudeReading.Bucket).ThenBy(q => q.Bucket).ThenBy(q => q.Minutes).Cast<Quota?>().ToArray();
        if (windows.Length == 0) windows = new Quota?[] { null };
        string state = $"{IsStale(now)}:{snapshot?.Allowed}:{ClaudeStale(now)}:" + string.Join("|", windows.Select(q => $"{q?.Bucket}:{q?.Label}:{q?.Minutes}:{q?.Remaining}:{q?.Reset:O}:{q?.Countdown(now)}"));
        if (state == segmentedState) return;
        segmentedState = state;
        SegmentRows.Children.Clear();
        foreach (var q in windows)
            SegmentRows.Children.Add(SegmentView.CreateRow(q, windows.Length == 1, q?.Bucket == ClaudeReading.Bucket ? ClaudeStale(now) : IsStale(now), q?.Bucket != ClaudeReading.Bucket && snapshot?.Allowed == false, now));
    }
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleCompactView(); return; }
        if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } KeepVisible(); if (!testing) Save(); }
    }
    private ConnectionWindow? connectionWindow;
    private void OpenConnectionSettings()
    {
        if (connectionWindow != null) { connectionWindow.Activate(); return; }
        connectionWindow = new ConnectionWindow(client, path => { prefs.CodexPath = path; Save(); });
        connectionWindow.Closed += (_, _) => { connectionWindow = null; nextPoll = DateTimeOffset.MinValue; };
        connectionWindow.Show();
    }
    public void Exit() { connectionWindow?.Close(); exiting = true; Close(); }
    public void SetTaskbar(bool enabled, bool save = true)
    {
        prefs.Taskbar = enabled;
        if (enabled && taskbarHost == null)
        {
            taskbarHost = new TaskbarHost(
                () => Dispatcher.BeginInvoke(() => { SetView(WindowView.Detail); ShowWidget(); }),
                () => Dispatcher.BeginInvoke(() => SetTaskbar(false)),
                () => Dispatcher.BeginInvoke(async () => { await RefreshAsync(); await PollClaudeAsync(true); }));
        }
        if (!enabled) { taskbarHost?.Dispose(); taskbarHost = null; }
        UpdateTaskbar();
        if (save && !testing) Save();
    }
    // Claude uses the same interval as Codex (1, 2 or 5 minutes). A report newer than one interval
    // (statusLine or an earlier poll) makes a request unnecessary; an expired window asks again once.
    private async Task PollClaudeAsync(bool force = false)
    {
        var now = DateTimeOffset.Now;
        if (claudeBusy || exiting) return;
        var interval = TimeSpan.FromSeconds(prefs.IntervalSeconds);
        // Re-ask once after a reset passes, never in a loop while the CLI keeps failing.
        var passed = claude?.Windows.Where(q => q.ResetPending(now)).Select(q => q.Reset!.Value).ToArray() ?? Array.Empty<DateTimeOffset>();
        bool expired = passed.Length > 0 && lastClaudeAttempt < passed.Max();
        if (!force && !expired)
        {
            if (now - lastClaudeAttempt < interval) return;
            if (claude != null && now - claude.Updated < interval - TimeSpan.FromSeconds(5)) return;
        }
        claudeBusy = true; lastClaudeAttempt = now; SetDetailRefreshing(true);
        try
        {
            var reading = await claudePoller.PollAsync(lifetime.Token);
            if (reading != null) { claudeStore.Refresh(); claude = claudeStore.Current ?? reading; CheckClaudeAlert(); }
        }
        catch (OperationCanceledException) { }
        finally
        {
            claudeBusy = false; SetDetailRefreshing(busy);
            if (!exiting) { RenderState(); UpdateTray(); UpdateTaskbar(); }
        }
    }
    private void UpdateTaskbar()
    {
        if (taskbarHost == null) return;
        try { var now = DateTimeOffset.Now; taskbarHost.Update(snapshot, IsStale(now), claude, ClaudeStale(now)); }
        catch { taskbarHost.ReportFailure(); }
    }
    private void UpdateProgress() { if (ProgressFill != null) ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * (snapshot?.Main?.Remaining ?? 0) / 100); }
    private void ProgressSizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgress();
    private void OpenSettings(object sender, RoutedEventArgs e) => ShowSettings();
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void CompactClick(object sender, RoutedEventArgs e) => ToggleCompactView();
    private async void RefreshClick(object sender, RoutedEventArgs e) { await RefreshAsync(); await PollClaudeAsync(true); }
    public void Capture(string path)
    {
        UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * 2), (int)Math.Ceiling(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(path); encoder.Save(file);
    }
}
