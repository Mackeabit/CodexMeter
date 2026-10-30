using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Forms = System.Windows.Forms;

namespace CodexMeter;

internal sealed record SettingsEntry(string Title, Action? Action = null, bool? Checked = null,
    IReadOnlyList<SettingsEntry>? Children = null, bool Enabled = true, bool Information = false);

public partial class MainWindow
{
    public void SetView(WindowView view, bool save = true)
    {
        prefs.SetView(view);
        ApplyView(save);
    }

    private void ToggleCompactView() => SetView(prefs.View == WindowView.Detail ? WindowView.Compact : WindowView.Detail);
    private void ToggleWindow() { if (IsVisible) Hide(); else ShowWidget(); }

    // Both menu surfaces and editions use the same actions, grouping and state.
    internal IReadOnlyList<SettingsEntry> SettingsEntries()
    {
        var taskbar = new List<SettingsEntry> {
            new("분할 막대 표시", () => SetTaskbar(!prefs.Taskbar), prefs.Taskbar)
        };
        string taskbarStatus = prefs.Taskbar ? taskbarHost?.Status ?? "연결 대기 중" : "꺼짐";
        taskbar.Add(new("상태: " + taskbarStatus, Information: true));

        var views = new List<SettingsEntry>();
        foreach (var (view, title) in new[] {
            (WindowView.Detail, "상세"), (WindowView.Compact, "작게"), (WindowView.Segmented, "막대") })
            views.Add(new(title, () => { SetView(view); ShowWidget(); }, prefs.View == view));

        var opacity = new List<SettingsEntry>();
        foreach (double value in new[] { 1d, .9, .8 })
            opacity.Add(new($"{value * 100:0}%", () => { Opacity = prefs.Opacity = value; Save(); }, Math.Abs(prefs.Opacity - value) < .01));

        var window = new List<SettingsEntry> {
            new(IsVisible ? "별도 창 숨기기" : "별도 창 표시", ToggleWindow),
            new("표시 방식", Children: views),
            new("항상 위에 표시", () => { Topmost = prefs.Topmost = !prefs.Topmost; Save(); }, prefs.Topmost),
            new("불투명도", Children: opacity),
            new("위치 초기화", () => { ShowWidget(); PlaceDefault(); Save(); })
        };
        var notifications = new List<SettingsEntry> {
            new("잔여량 20%·5% 알림", ToggleAlerts, prefs.Alerts)
        };
        if (prefs.Alerts) notifications.Add(new(AlertPauseTitle, ToggleAlertPause, Enabled: alerts.HasQuotas(snapshot)));
        if (alerts.StorageWarning != null) notifications.Add(new(alerts.StorageWarning, Enabled: false));

        var intervals = new List<SettingsEntry>();
        foreach (int seconds in new[] { 60, 120, 300 })
            intervals.Add(new($"{seconds / 60}분마다 갱신", () => {
                prefs.IntervalSeconds = seconds; nextPoll = DateTimeOffset.Now.AddSeconds(seconds); Save(); RenderState();
            }, prefs.IntervalSeconds == seconds));

        return new SettingsEntry[] {
            new("연결 및 자동 실행 설정…", OpenConnectionSettings),
            new("새로고침", () => _ = RefreshAsync()),
            new("작업표시줄 설정", Children: taskbar),
            new("별도 창 설정", Children: window),
            new("알림 설정", Children: notifications),
            new("갱신 주기", Children: intervals),
            new("종료", Exit)
        };
    }

    private void CreateTray()
    {
        tray = new Forms.NotifyIcon { Text = "Codex Meter · 연결 중", Visible = true };
        UpdateTray();
        tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ToggleWindow); };
        var menu = BuildTrayMenu();
        tray.ContextMenuStrip = menu;
        Closed += (_, _) => menu.Dispose();
    }

    internal Forms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        void Populate()
        {
            foreach (var item in menu.Items.Cast<Forms.ToolStripItem>().ToArray()) {
                menu.Items.Remove(item); item.Dispose();
            }
            foreach (var entry in SettingsEntries()) menu.Items.Add(TrayMenuItem(entry));
        }
        Populate();
        menu.Opening += (_, _) => Populate();
        return menu;
    }

    private Forms.ToolStripMenuItem TrayMenuItem(SettingsEntry entry)
    {
        var item = new Forms.ToolStripMenuItem(entry.Title) { Checked = entry.Checked ?? false, Enabled = entry.Enabled };
        if (entry.Action != null) item.Click += (_, _) => Dispatcher.Invoke(entry.Action);
        if (entry.Children != null)
            foreach (var child in entry.Children) {
                if (child.Information) {
                    item.DropDownItems.Add(new Forms.ToolStripSeparator());
                    item.DropDownItems.Add(new Forms.ToolStripLabel(child.Title) {
                        ForeColor = System.Drawing.SystemColors.MenuText,
                        Padding = new Forms.Padding(4, 4, 4, 4),
                        AccessibleRole = Forms.AccessibleRole.StaticText
                    });
                }
                else item.DropDownItems.Add(TrayMenuItem(child));
            }
        return item;
    }

    internal ContextMenu BuildSettingsMenu()
    {
        var menu = new ContextMenu { Background = Brush("#FFFFFF"), Foreground = Brush("#181818"), BorderBrush = Brush("#D5D5D5"), FontSize = 13 };
        foreach (var entry in SettingsEntries()) menu.Items.Add(WindowMenuItem(entry));
        return menu;
    }

    private static MenuItem WindowMenuItem(SettingsEntry entry)
    {
        var item = new MenuItem { Header = entry.Title, IsCheckable = entry.Checked.HasValue,
            IsChecked = entry.Checked ?? false, IsEnabled = entry.Enabled };
        if (entry.Information) {
            // A plain text footer: no disabled tint, check gutter, hover or keyboard selection.
            item.Focusable = false; item.IsTabStop = false; item.IsHitTestVisible = false;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BorderBrushProperty, Brush("#D5D5D5"));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(0, 1, 0, 0));
            border.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 6));
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.ForegroundProperty, Brush("#181818"));
            text.SetBinding(TextBlock.TextProperty, new Binding("Header") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(text);
            item.Template = new ControlTemplate(typeof(MenuItem)) { VisualTree = border };
            return item;
        }
        if (entry.Action != null) item.Click += (_, e) => { e.Handled = true; entry.Action(); };
        if (entry.Children != null)
            foreach (var child in entry.Children) item.Items.Add(WindowMenuItem(child));
        return item;
    }

    private void ShowSettings()
    {
        var menu = BuildSettingsMenu();
        menu.PlacementTarget = this; menu.IsOpen = true;
    }
}
