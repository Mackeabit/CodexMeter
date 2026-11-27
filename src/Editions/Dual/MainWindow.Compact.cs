using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexMeter;

/// <summary>
/// Dual's compact ("작게") view: Codex and Claude side by side, each a small ring with the same center rule as the
/// taskbar (5-hour first), the window that number comes from, and its countdown. Replaces the shared Codex-only
/// compact layout, which stays untouched for Classic and Expanded.
/// </summary>
public partial class MainWindow
{
    internal Grid? DualCompact { get; private set; }
    internal StackPanel? CompactCodex { get; private set; }
    internal StackPanel? CompactClaude { get; private set; }
    private string? compactState;
    private const double CompactRing = 44;

    private void BuildCompactView()
    {
        var grid = new Grid { Margin = new Thickness(14, 10, 8, 10), Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.SizeAll, Visibility = Visibility.Collapsed };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(17) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.MouseLeftButtonDown += DragWindow;
        CompactCodex = new StackPanel { Orientation = Orientation.Horizontal };
        CompactClaude = new StackPanel { Orientation = Orientation.Horizontal };
        Grid.SetColumn(CompactClaude, 2);
        var divider = new Border { Width = 1, Background = Brush("#E8E8E8"), Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(CompactCodex);
        grid.Children.Add(divider);
        grid.Children.Add(CompactClaude);
        var expand = HeaderButton("↗", "상세 보기", 3, CompactClick, 19, new Thickness(6, 3, 6, 3));
        expand.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(expand);
        DualCompact = grid;
        ((Grid)Outer.Child).Children.Add(grid);
    }

    private void RenderCompact(DateTimeOffset now)
    {
        if (DualCompact == null || CompactCodex == null || CompactClaude == null) return;
        bool codexOld = IsStale(now), claudeOld = ClaudeStale(now);
        var services = TaskbarHost.Services(snapshot, codexOld, claude, now, claudeOld);
        string codexNote = error != null ? "갱신 실패" : snapshot == null ? "연결 중" : snapshot.Allowed == false ? "사용 제한" : codexOld ? "이전 값" : "";
        string claudeNote = claude == null ? (claudePoller.LastError != null ? "연결 안 됨" : "확인 중") : claudeOld ? "이전 값" : "";
        string Key(RingService s) => $"{s.Center}:{s.Stale}:{s.Warning}:{s.Shown?.Minutes}:{s.NoFiveHour}:{CompactLines(s, now).Item1}:{CompactLines(s, now).Item2}";
        string state = string.Join("|", services.Select(Key)) + $"|{codexNote}|{claudeNote}";
        if (state == compactState) return;
        compactState = state;
        FillCompact(CompactCodex, services[0], codexNote, now);
        FillCompact(CompactClaude, services[1], claudeNote, now);
    }

    // Second line: which window the number is (5시간/주간) or 사용 불가; third line: time to that reset or to resuming.
    private static (string Window, string Time) CompactLines(RingService s, DateTimeOffset now)
    {
        if (s.Warning) return ("사용 제한", "");
        if (s.Blocking is Quota b) return ("사용 불가", $"{RingArtwork.ShortCountdown(b, now)} 후 재개");
        if (s.Shown is not Quota q) return ("", "");
        return (q.Duration, q.Reset is null ? "리셋 미제공" : q.ResetPending(now) ? "리셋 확인 중" : $"{RingArtwork.ShortCountdown(q, now)} 후 리셋");
    }

    private void FillCompact(StackPanel cell, RingService s, string note, DateTimeOffset now)
    {
        cell.Children.Clear();
        var ring = new System.Windows.Controls.Image { Width = CompactRing, Height = CompactRing, Source = RingImage(s, CompactRing), VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
        cell.Children.Add(ring);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        // The service tone that keeps about 4.5:1 contrast on the white window.
        var accent = RingArtwork.TextAccent(s, light: true);
        text.Children.Add(new TextBlock { Text = s.Short, FontSize = 12, FontWeight = FontWeights.SemiBold });
        var (window, time) = CompactLines(s, now);
        bool alert = s.Blocked || s.Warning;
        // A stale or failed service says so instead of the countdown, like the detail view's status slot.
        if (note.Length > 0 && !alert) time = note;
        if (window.Length > 0) text.Children.Add(new TextBlock { Text = window, FontSize = 11, FontWeight = alert ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = alert ? Brush(LowText) : s.Stale ? Brush(StaleText) : new SolidColorBrush(Color.FromRgb(accent.R, accent.G, accent.B)), Margin = new Thickness(0, 1, 0, 0) });
        if (time.Length > 0) text.Children.Add(new TextBlock { Text = time, FontSize = 11, Foreground = Brush(MutedText) });
        cell.Children.Add(text);
        string name = $"{s.Short} {window} {s.Center}" + (s.Shown != null && !s.Warning ? "% 남음" : "") + (time.Length > 0 ? $", {time}" : "");
        System.Windows.Automation.AutomationProperties.SetName(cell, name);
        cell.ToolTip = s.NoFiveHour ? "5시간 한도 없음 (주간 한도만 있음)" : null;
    }
}
