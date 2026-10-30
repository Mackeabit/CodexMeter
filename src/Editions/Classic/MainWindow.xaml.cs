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

public sealed class Preferences
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Compact { get; set; }
    public bool Segmented { get; set; }
    public bool Taskbar { get; set; } = true;
    public string? CodexPath { get; set; }
    public bool Topmost { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public int IntervalSeconds { get; set; } = 120;
    public bool Alerts { get; set; } = false;
    public string TrayStyle { get; set; } = "number";
}

public partial class MainWindow : Window
{
    private readonly bool testing;
    private readonly UsageClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private Forms.NotifyIcon? tray;
    private TaskbarHost? taskbarHost;
    private Forms.ToolStripMenuItem? taskbarItem;
    private Drawing.Icon? currentIcon;
    private Snapshot? snapshot;
    private bool busy, exiting;
    private string? trayState;
    private string? segmentedState;
    private Forms.ToolStripMenuItem? numberStyleItem, batteryStyleItem;
    private string? error;
    private DateTimeOffset nextPoll = DateTimeOffset.MinValue;
    private readonly UsageAlerts alerts;
    private Preferences prefs = new();
    private readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMeter", "settings.json");
    private static Brush Brush(string color) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    public MainWindow(bool testing = false)
    {
        this.testing = testing;
        alerts = new UsageAlerts(testing ? null : UsageAlerts.DefaultPath);
        InitializeComponent();
        if (!testing) try { if (File.Exists(settingsFile)) prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(settingsFile)) ?? new(); } catch { }
        prefs.IntervalSeconds = prefs.IntervalSeconds is 60 or 120 or 300 ? prefs.IntervalSeconds : 120;
        prefs.TrayStyle = prefs.TrayStyle == "battery" ? "battery" : "number";
        prefs.Opacity = double.IsFinite(prefs.Opacity) ? Math.Clamp(prefs.Opacity, .75, 1) : 1;
        Topmost = prefs.Topmost; Opacity = prefs.Opacity;
        client.PreferredPath = prefs.CodexPath;
        ApplyView(false);
        Loaded += async (_, _) =>
        {
            PlaceInitial(); KeepVisible();
            if (!testing) { CreateTray(); if (prefs.Taskbar) { SetTaskbar(true, false); Hide(); } clock.Start(); await RefreshAsync(); }
        };
        clock.Tick += async (_, _) => { RenderState(); UpdateTray(); UpdateTaskbar(); if (!busy && DateTimeOffset.Now >= nextPoll) await RefreshAsync(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); if (e.Key == Key.F5) _ = RefreshAsync(); };
        MouseRightButtonUp += (_, _) => ShowSettings();
        Closing += (_, e) => { if (!exiting) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { lifetime.Cancel(); clock.Stop(); taskbarHost?.Dispose(); tray?.Dispose(); currentIcon?.Dispose(); if (!testing) Save(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
    }
    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { PlaceDefault(); Save(); });
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
    private bool IsStale(DateTimeOffset now) => error != null || snapshot?.Main?.ResetPending(now) == true || (snapshot != null && now - snapshot.Fetched > TimeSpan.FromSeconds(Math.Max(180, prefs.IntervalSeconds * 2 + 15)));
    public void ShowWidget() { Show(); WindowState = WindowState.Normal; }
    private void Save()
    {
        prefs.Left = Left; prefs.Top = Top;
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); var temp = settingsFile + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(prefs)); File.Move(temp, settingsFile, true); } catch { }
    }
    private void CreateTray()
    {
        tray = new Forms.NotifyIcon { Text = "Codex Meter · 연결 중", Visible = true };
        UpdateTray();
        tray.MouseClick += (_, e) => Dispatcher.Invoke(() => { if (e.Button == Forms.MouseButtons.Left) { if (IsVisible) Hide(); else ShowWidget(); } });
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("위젯 표시 / 숨기기", null, (_, _) => Dispatcher.Invoke(() => { if (IsVisible) Hide(); else ShowWidget(); }));
        menu.Items.Add("연결 및 자동 실행 설정…", null, (_, _) => Dispatcher.Invoke(OpenConnectionSettings));
        menu.Items.Add("새로고침", null, (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAsync()));
        var alertsItem = new Forms.ToolStripMenuItem("잔여량 20%·5% 알림");
        alertsItem.Click += (_, _) => Dispatcher.Invoke(ToggleAlerts);
        var pauseItem = new Forms.ToolStripMenuItem();
        pauseItem.Click += (_, _) => Dispatcher.Invoke(ToggleAlertPause);
        var alertStatus = new Forms.ToolStripMenuItem { Enabled = false };
        menu.Items.Add(alertsItem);
        menu.Items.Add(pauseItem);
        menu.Items.Add(alertStatus);
        menu.Opening += (_, _) => {
            alertsItem.Checked = prefs.Alerts;
            pauseItem.Text = AlertPauseTitle;
            pauseItem.Enabled = prefs.Alerts && alerts.HasQuotas(snapshot);
            alertStatus.Visible = alerts.StorageWarning != null;
            alertStatus.Text = alerts.StorageWarning ?? "";
        };
        var styleMenu = new Forms.ToolStripMenuItem("아이콘 디자인");
        numberStyleItem = new Forms.ToolStripMenuItem("숫자만") { Checked = prefs.TrayStyle == "number" };
        batteryStyleItem = new Forms.ToolStripMenuItem("배터리 + 숫자") { Checked = prefs.TrayStyle == "battery" };
        numberStyleItem.Click += (_, _) => Dispatcher.Invoke(() => SetTrayStyle("number"));
        batteryStyleItem.Click += (_, _) => Dispatcher.Invoke(() => SetTrayStyle("battery"));
        styleMenu.DropDownItems.Add(numberStyleItem);
        styleMenu.DropDownItems.Add(batteryStyleItem);
        menu.Items.Add(styleMenu);
        taskbarItem = new Forms.ToolStripMenuItem("작업표시줄 분할 막대") { Checked = prefs.Taskbar };
        taskbarItem.Click += (_, _) => Dispatcher.Invoke(() => SetTaskbar(!prefs.Taskbar));
        menu.Items.Add(taskbarItem);
        var taskbarStatusItem = new Forms.ToolStripMenuItem { Enabled = false };
        menu.Items.Add(taskbarStatusItem);
        menu.Opening += (_, _) => { taskbarStatusItem.Visible = prefs.Taskbar; taskbarStatusItem.Text = taskbarHost?.Status ?? "작업표시줄 연결 대기"; };
        menu.Items.Add("가로 분할 막대 위젯", null, (_, _) => Dispatcher.Invoke(() => { SetSegmented(true); ShowWidget(); }));
        menu.Items.Add("상세 화면", null, (_, _) => Dispatcher.Invoke(() => { SetCompact(false); ShowWidget(); }));
        menu.Items.Add("작게 / 크게", null, (_, _) => Dispatcher.Invoke(() => { SetCompact(!prefs.Compact); ShowWidget(); }));
        menu.Items.Add("위치 초기화", null, (_, _) => Dispatcher.Invoke(() => { ShowWidget(); PlaceDefault(); Save(); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(Exit));
        tray.ContextMenuStrip = menu;
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    private void UpdateTray()
    {
        if (tray == null) return;
        var main = snapshot?.Main;
        bool stale = IsStale(DateTimeOffset.Now);
        string text = stale ? "!" : main == null ? "—" : Math.Floor(main.Remaining).ToString();
        bool light = TrayArtwork.IsLightTaskbar();
        int iconSize = Math.Clamp(Forms.SystemInformation.SmallIconSize.Width, 16, 64);
        var state = $"{text}:{stale}:{main?.Duration}:{snapshot?.Allowed}:{light}:{iconSize}:{prefs.TrayStyle}:{main?.Remaining}";
        if (state == trayState) return;
        trayState = state;
        using var bitmap = TrayArtwork.Render(text, iconSize, light, stale, prefs.TrayStyle, main?.Remaining);
        var h = bitmap.GetHicon();
        using var borrowed = Drawing.Icon.FromHandle(h);
        var icon = (Drawing.Icon)borrowed.Clone();
        DestroyIcon(h);
        tray.Icon = icon;
        currentIcon?.Dispose(); currentIcon = icon;
        var label = main == null ? "사용량 미확인" : $"{main.Duration} {main.Remaining:0.#}% 남음";
        tray.Text = $"Codex Meter · {label}" + (stale ? " · 갱신 확인 필요" : "");
    }
    public async Task RefreshAsync()
    {
        if (busy || testing || exiting) return;
        busy = true; RefreshButton.IsEnabled = false;
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
            RefreshButton.IsEnabled = true; RenderState(); UpdateTray(); UpdateTaskbar();
        }
    }
    private void CheckAlert()
    {
        var notification = alerts.Observe(snapshot, DateTimeOffset.Now, prefs.Alerts && tray != null,
            Math.Max(180, prefs.IntervalSeconds * 2 + 15));
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
    }
    private string AlertPauseTitle => alerts.AnyMuted(snapshot) ? "알림 다시 켜기" : "다음 리셋까지 알림 끄기";
    private void ToggleAlertPause()
    {
        alerts.SetMuted(snapshot, DateTimeOffset.Now, !alerts.AnyMuted(snapshot));
        nextPoll = DateTimeOffset.MinValue;
    }
    public void SetSnapshot(Snapshot? value, string? failure = null) { snapshot = value; error = failure; RenderState(); }
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
        foreach (var w in snapshot?.Windows.Where(x => !ReferenceEquals(x, q)) ?? Enumerable.Empty<Quota>())
        {
            var row = new Grid { Margin = new Thickness(2, 5, 2, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = (w.Bucket == "codex" ? "" : w.Label + " · ") + w.Duration, FontSize = 12, Foreground = Brush("#666666"), TextTrimming = TextTrimming.CharacterEllipsis };
            var amount = new TextBlock { Text = $"{w.Remaining:0.#}% 남음", FontSize = 12, Foreground = Brush("#181818"), Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(amount, 1); row.Children.Add(label); row.Children.Add(amount);
            row.ToolTip = $"{w.Countdown(now)} · {w.Reset?.ToLocalTime():M/d HH:mm}";
            ExtraWindows.Children.Add(row);
        }
        ExtraWindows.Visibility = ExtraWindows.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderSegments();
    }
    public void SetCompact(bool value, bool save = true)
    {
        prefs.Segmented = false;
        prefs.Compact = value;
        ApplyView(save);
    }
    public void SetSegmented(bool value, bool save = true)
    {
        prefs.Segmented = value;
        ApplyView(save);
    }
    private void ApplyView(bool save)
    {
        double oldHeight = ActualHeight;
        bool bars = prefs.Segmented;
        Expanded.Visibility = !bars && !prefs.Compact ? Visibility.Visible : Visibility.Collapsed;
        Compact.Visibility = !bars && prefs.Compact ? Visibility.Visible : Visibility.Collapsed;
        SegmentRows.Visibility = bars ? Visibility.Visible : Visibility.Collapsed;
        Outer.Margin = new Thickness(bars ? 3 : 10);
        Outer.CornerRadius = new CornerRadius(bars ? 5 : 13);
        Outer.Background = Brush(bars ? "#252525" : "#FFFFFF");
        Outer.BorderBrush = Brush(bars ? "#3D3D3D" : "#D5D5D5");
        Outer.Effect = bars ? null : new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 16, ShadowDepth = 3, Opacity = .14 };
        Width = bars ? 278 : prefs.Compact ? 330 : 348;
        RenderSegments();
        if (IsLoaded) { UpdateLayout(); Top = Math.Max(SystemParameters.VirtualScreenTop, Top + oldHeight - ActualHeight); KeepVisible(); }
        if (save && !testing) Save();
    }
    private void RenderSegments()
    {
        if (!prefs.Segmented) return;
        var now = DateTimeOffset.Now;
        var windows = snapshot?.Windows.OrderBy(q => q.Bucket).ThenBy(q => q.Minutes).Cast<Quota?>().ToArray() ?? Array.Empty<Quota?>();
        if (windows.Length == 0) windows = new Quota?[] { null };
        string state = $"{IsStale(now)}:{snapshot?.Allowed}:" + string.Join("|", windows.Select(q => $"{q?.Bucket}:{q?.Label}:{q?.Minutes}:{q?.Remaining}:{q?.Reset:O}:{q?.Countdown(now)}"));
        if (state == segmentedState) return;
        segmentedState = state;
        SegmentRows.Children.Clear();
        foreach (var q in windows)
        {
            bool stale = IsStale(now) || q?.ResetPending(now) == true;
            bool denied = snapshot?.Allowed == false;
            var row = new Grid { Height = 23, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            string amount = stale || denied ? "!" : q == null ? "—" : $"{Math.Floor(q.Remaining):0}%";
            string description = q == null ? "조회 가능한 사용량이 없습니다" : $"{q.Label} · {q.Duration} · {q.Remaining:0.#}% 남음 · {q.Countdown(now)}";
            row.ToolTip = UsageHover.Describe(q, stale, denied, now) + "\nPC 현지시간 · 끌어서 이동 · 두 번 클릭하면 상세";
            ToolTipService.SetInitialShowDelay(row, 400);
            ToolTipService.SetShowDuration(row, 30000);
            System.Windows.Automation.AutomationProperties.SetName(row, description);
            var cells = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 10; i++)
            {
                double filled = q == null ? 0 : Math.Clamp(q.Remaining / 10 - i, 0, 1);
                var cell = new Grid { Width = 10, Height = 13, Margin = new Thickness(0, 0, 2, 0) };
                cell.Children.Add(new Border { Background = Brush("#464646"), CornerRadius = new CornerRadius(2) });
                var fill = new Border { Background = Brush(stale || denied ? "#858585" : "#DE805E"), CornerRadius = new CornerRadius(2) };
                fill.Clip = new RectangleGeometry(new Rect(0, 0, 10 * filled, 13));
                cell.Children.Add(fill);
                cells.Children.Add(cell);
            }
            row.Children.Add(cells);
            var number = new TextBlock { Text = amount, Foreground = Brush(stale || denied ? "#D0D0D0" : "#E4E4E4"), FontSize = 12, FontFamily = new FontFamily("Segoe UI"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(number, 1);
            row.Children.Add(number);
            var reset = new TextBlock { Text = TaskbarHost.ResetText(q), FontSize = 12, FontFamily = new FontFamily("Segoe UI"), Foreground = Brush("#E4E4E4"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(reset, 2);
            row.Children.Add(reset);
            SegmentRows.Children.Add(row);
        }
    }
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { SetCompact(prefs.Segmented ? false : !prefs.Compact); return; }
        if (e.LeftButton == MouseButtonState.Pressed) { try { DragMove(); } catch { } KeepVisible(); if (!testing) Save(); }
    }
    private void ShowSettings()
    {
        var menu = new ContextMenu { Background = Brush("#FFFFFF"), Foreground = Brush("#181818"), BorderBrush = Brush("#D5D5D5"), FontSize = 13 };
        void Add(string title, Action action, bool? check = null)
        {
            var item = new MenuItem { Header = title, IsCheckable = check.HasValue, IsChecked = check ?? false };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        Add("연결 및 자동 실행 설정…", OpenConnectionSettings);
        menu.Items.Add(new Separator());
        Add("항상 위에 표시", () => { Topmost = prefs.Topmost = !prefs.Topmost; Save(); }, prefs.Topmost);
        Add("작업표시줄 분할 막대", () => SetTaskbar(!prefs.Taskbar), prefs.Taskbar);
        if (prefs.Taskbar && taskbarHost != null) { var status = new MenuItem { Header = taskbarHost.Status, IsEnabled = false }; menu.Items.Add(status); }
        Add("작은 표시창", () => SetCompact(!prefs.Compact), prefs.Compact);
        Add("가로 분할 막대 위젯", () => SetSegmented(!prefs.Segmented), prefs.Segmented);
        if (prefs.Segmented) Add("상세 화면", () => SetCompact(false));
        Add("잔여량 20%·5% 알림", ToggleAlerts, prefs.Alerts);
        var pauseItem = new MenuItem { Header = AlertPauseTitle, IsEnabled = prefs.Alerts && alerts.HasQuotas(snapshot) };
        pauseItem.Click += (_, _) => ToggleAlertPause();
        menu.Items.Add(pauseItem);
        if (alerts.StorageWarning != null) menu.Items.Add(new MenuItem { Header = alerts.StorageWarning, IsEnabled = false });
        var styleMenu = new MenuItem { Header = "아이콘 디자인" };
        foreach (var (key, title) in new[] { ("number", "숫자만"), ("battery", "배터리 + 숫자") })
        {
            var item = new MenuItem { Header = title, IsCheckable = true, IsChecked = prefs.TrayStyle == key };
            item.Click += (_, _) => SetTrayStyle(key);
            styleMenu.Items.Add(item);
        }
        menu.Items.Add(styleMenu);
        menu.Items.Add(new Separator());
        foreach (var n in new[] { 60, 120, 300 }) Add($"{n / 60}분마다 갱신", () => { prefs.IntervalSeconds = n; nextPoll = DateTimeOffset.Now.AddSeconds(n); Save(); RenderState(); }, prefs.IntervalSeconds == n);
        menu.Items.Add(new Separator());
        foreach (var o in new[] { 1d, .9, .8 }) Add($"불투명도 {o * 100:0}%", () => { Opacity = prefs.Opacity = o; Save(); }, Math.Abs(prefs.Opacity - o) < .01);
        menu.Items.Add(new Separator());
        Add("위치 초기화", () => { PlaceDefault(); Save(); });
        Add("종료", Exit);
        menu.PlacementTarget = this; menu.IsOpen = true;
    }
    private ConnectionWindow? connectionWindow;
    private void OpenConnectionSettings()
    {
        if (connectionWindow != null) { connectionWindow.Activate(); return; }
        connectionWindow = new ConnectionWindow(client, path => { prefs.CodexPath = path; Save(); }, Exit);
        connectionWindow.Closed += (_, _) => { connectionWindow = null; nextPoll = DateTimeOffset.MinValue; };
        connectionWindow.Show();
    }
    public void Exit() { connectionWindow?.Close(); exiting = true; Close(); }
    public void SetTaskbar(bool enabled, bool save = true)
    {
        prefs.Taskbar = enabled;
        if (taskbarItem != null) taskbarItem.Checked = enabled;
        if (enabled && taskbarHost == null)
        {
            taskbarHost = new TaskbarHost(
                () => Dispatcher.BeginInvoke(() => { SetCompact(false); ShowWidget(); }),
                () => Dispatcher.BeginInvoke(() => SetTaskbar(false)),
                () => Dispatcher.BeginInvoke(async () => await RefreshAsync()));
        }
        if (!enabled) { taskbarHost?.Dispose(); taskbarHost = null; }
        UpdateTaskbar();
        if (save && !testing) Save();
    }
    private void UpdateTaskbar()
    {
        if (taskbarHost == null) return;
        try { taskbarHost.Update(snapshot, IsStale(DateTimeOffset.Now)); }
        catch { taskbarHost.ReportFailure(); }
    }
    public void SetTrayStyle(string style)
    {
        prefs.TrayStyle = style == "battery" ? "battery" : "number";
        if (numberStyleItem != null) numberStyleItem.Checked = prefs.TrayStyle == "number";
        if (batteryStyleItem != null) batteryStyleItem.Checked = prefs.TrayStyle == "battery";
        trayState = null;
        UpdateTray();
        if (!testing) Save();
    }
    private void UpdateProgress() { if (ProgressFill != null) ProgressFill.Width = Math.Max(0, ProgressTrack.ActualWidth * (snapshot?.Main?.Remaining ?? 0) / 100); }
    private void ProgressSizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgress();
    private void OpenSettings(object sender, RoutedEventArgs e) => ShowSettings();
    private void HideClick(object sender, RoutedEventArgs e) => Hide();
    private void CompactClick(object sender, RoutedEventArgs e) => SetCompact(!prefs.Compact);
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    public void Capture(string path)
    {
        UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * 2), (int)Math.Ceiling(ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var file = File.Create(path); encoder.Save(file);
    }
}
