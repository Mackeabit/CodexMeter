using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CodexMeter;

/// <summary>
/// Dual's detail window: Codex and Claude as two equal sections (ring, 5-hour and weekly rows),
/// instead of the shared Codex-first layout. Built in code so Classic and Expanded keep their XAML.
/// </summary>
public partial class MainWindow
{
    internal StackPanel? DetailPanel { get; private set; }
    internal int RefreshSeconds => prefs.IntervalSeconds;
    internal StackPanel? CodexSection { get; private set; }
    internal StackPanel? ClaudeSection { get; private set; }
    internal TextBlock? DetailStateText { get; private set; }
    internal TextBlock? DetailUpdatedText { get; private set; }
    internal Border? DetailErrorPanel { get; private set; }
    private TextBlock? detailErrorText;
    private Ellipse? detailStateDot;
    private Button? detailRefresh;
    private string? detailState;

    private const string LowText = "#C93B3A", MutedText = "#666666", InkText = "#181818", StaleText = "#8A8A8A";

    private void BuildDetailView()
    {
        var panel = new StackPanel { Margin = new Thickness(22, 17, 22, 14) };

        var header = new Grid { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.SizeAll };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += DragWindow;
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "사용량", FontSize = 17, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "Codex · Claude", FontSize = 12, Foreground = Brush(MutedText), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 2) });
        header.Children.Add(title);
        header.Children.Add(HeaderButton("···", "설정", 1, OpenSettings));
        header.Children.Add(HeaderButton("−", "트레이로 숨기기", 2, HideClick));
        panel.Children.Add(header);

        CodexSection = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(CodexSection);
        panel.Children.Add(new Border { Height = 1, Background = Brush("#E8E8E8"), Margin = new Thickness(0, 16, 0, 16) });
        ClaudeSection = new StackPanel();
        panel.Children.Add(ClaudeSection);

        detailErrorText = new TextBlock { Foreground = Brush("#333333"), TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        DetailErrorPanel = new Border { Background = Brush("#F3F3F3"), BorderBrush = Brush("#D0D0D0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed, Child = detailErrorText };
        panel.Children.Add(DetailErrorPanel);
        panel.Children.Add(new Border { Height = 1, Background = Brush("#E8E8E8"), Margin = new Thickness(0, 18, 0, 11) });

        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var status = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var stateLine = new StackPanel { Orientation = Orientation.Horizontal };
        detailStateDot = new Ellipse { Width = 4, Height = 4, Fill = Brush("#333333"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) };
        DetailStateText = new TextBlock { Text = "연결 중", FontSize = 10, Foreground = Brush("#555555") };
        stateLine.Children.Add(detailStateDot); stateLine.Children.Add(DetailStateText);
        DetailUpdatedText = new TextBlock { Text = "첫 조회를 기다립니다", FontSize = 10, Foreground = Brush(MutedText), Margin = new Thickness(0, 3, 0, 0) };
        status.Children.Add(stateLine); status.Children.Add(DetailUpdatedText);
        footer.Children.Add(status);
        detailRefresh = HeaderButton("↻", "새로고침", 1, RefreshClick, 20, new Thickness(7, 3, 7, 3));
        footer.Children.Add(detailRefresh);
        footer.Children.Add(HeaderButton("↗", "작게 보기", 2, CompactClick, 19, new Thickness(7, 3, 7, 3)));
        panel.Children.Add(footer);

        DetailPanel = panel;
        ((Grid)Outer.Child).Children.Add(panel);
    }

    private static Button HeaderButton(string text, string name, int column, RoutedEventHandler click, double size = 20, Thickness? padding = null)
    {
        var button = new Button { Content = text, FontSize = size, Padding = padding ?? new Thickness(7, 0, 7, 0), ToolTip = name };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += click;
        Grid.SetColumn(button, column);
        return button;
    }

    private void SetDetailRefreshing(bool refreshing) { if (detailRefresh != null) detailRefresh.IsEnabled = !refreshing; }

    private void RenderDetail(DateTimeOffset now)
    {
        if (DetailPanel == null || CodexSection == null || ClaudeSection == null) return;
        bool codexOld = IsStale(now), claudeOld = ClaudeStale(now);
        var services = TaskbarHost.Services(snapshot, codexOld, claude, now, claudeOld);
        string codexStatus = error != null ? "갱신 실패" : snapshot == null ? "연결 중" : snapshot.Allowed == false ? "사용 제한" : codexOld ? "이전 값" : $"{snapshot.Fetched.ToLocalTime():HH:mm} 확인";
        string claudeStatus = claude == null ? (claudePoller.LastError != null ? "연결 안 됨" : "확인 중") : claudeOld ? "이전 값" : $"{claude.Updated.ToLocalTime():HH:mm} 확인";
        string? codexExtra = snapshot?.Resets is int count ? $"보관 리셋 {count}회" : null;
        string plan = (snapshot?.Plan is string p && p != "계정" ? p : "").ToUpperInvariant();
        string errors = string.Join("\n", new[] { error, claude == null || claudeOld ? claudePoller.LastError : null }.Where(x => !string.IsNullOrEmpty(x)));
        bool anyOld = error != null || codexOld || claudeOld;
        string updated = $"{prefs.IntervalSeconds / 60}분마다 갱신 · Codex·Claude 함께";
        string state = string.Join("|", services.Select(s => $"{s.Center}:{s.Stale}:{s.Warning}:{Row(s.FiveHour, now)}:{Row(s.Weekly, now)}"))
            + $"|{codexStatus}|{claudeStatus}|{codexExtra}|{plan}|{errors}|{anyOld}|{updated}";
        if (state == detailState) return;
        detailState = state;

        FillSection(CodexSection, services[0], plan, codexStatus, codexExtra, now);
        FillSection(ClaudeSection, services[1], null, claudeStatus, claude == null ? "Claude Code CLI 로그인 후 표시됩니다" : null, now);
        DetailErrorPanel!.Visibility = errors.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        detailErrorText!.Text = errors;
        DetailStateText!.Text = error != null ? "갱신 실패" : anyOld ? "갱신 대기" : snapshot == null && claude == null ? "연결 중" : "동기화됨";
        detailStateDot!.Fill = Brush(anyOld ? "#777777" : "#333333");
        DetailUpdatedText!.Text = updated;
    }

    private static string Row(Quota? q, DateTimeOffset now) => q == null ? "-" : $"{Math.Floor(q.Remaining)}:{q.Countdown(now)}";

    // Ring on the left; name, plan and status on top; one row each for the 5-hour and weekly limits.
    private void FillSection(StackPanel section, RingService s, string? plan, string status, string? note, DateTimeOffset now)
    {
        section.Children.Clear();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var ring = new Image { Width = 64, Height = 64, Source = RingImage(s), VerticalAlignment = VerticalAlignment.Top, SnapsToDevicePixels = true };
        System.Windows.Automation.AutomationProperties.SetName(ring, $"{s.Short} {s.Center}% 남음");
        grid.Children.Add(ring);

        var body = new StackPanel();
        Grid.SetColumn(body, 2);
        var top = new Grid { Margin = new Thickness(0, 1, 0, 8) };
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var accent = s.Accent;
        name.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(Color.FromRgb(accent.R, accent.G, accent.B)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 7, 0) });
        name.Children.Add(new TextBlock { Text = s.Short, FontSize = 15, FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(plan))
            name.Children.Add(new Border { BorderBrush = Brush("#DDDDDD"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(8, 1, 0, 1), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = plan, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brush("#606060") } });
        top.Children.Add(name);
        var statusText = new TextBlock { Text = status, FontSize = 11, Foreground = Brush(MutedText), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(statusText, 1);
        top.Children.Add(statusText);
        body.Children.Add(top);
        body.Children.Add(LimitRow("5시간", s.FiveHour, s, now));
        body.Children.Add(LimitRow("주간", s.Weekly, s, now));
        if (!string.IsNullOrEmpty(note)) body.Children.Add(new TextBlock { Text = note, FontSize = 11, Foreground = Brush(MutedText), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(body);
        section.Children.Add(grid);
    }

    private UIElement LimitRow(string label, Quota? q, RingService s, DateTimeOffset now)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        bool low = q != null && !s.Warning && q.Remaining < RingArtwork.LowThreshold;
        string valueColor = s.Warning ? LowText : s.Stale ? StaleText : low ? LowText : InkText;
        row.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brush(MutedText), VerticalAlignment = VerticalAlignment.Center });
        var value = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Brush(valueColor), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
        value.Text = s.Warning ? "!" : q == null ? "—" : $"{Math.Floor(q.Remaining).ToString("0", CultureInfo.InvariantCulture)}%";
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        var reset = new TextBlock { FontSize = 11, Foreground = Brush(MutedText), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        reset.Text = q == null ? "정보 없음" : q.Countdown(now);
        Grid.SetColumn(reset, 2);
        row.Children.Add(reset);
        row.ToolTip = q?.Reset is DateTimeOffset at ? $"{at.ToLocalTime():M월 d일 HH:mm} 리셋 (PC 현지시간)" : "리셋 시간 미제공";
        System.Windows.Automation.AutomationProperties.SetName(row, $"{s.Short} {label} {value.Text} 남음, {reset.Text}");
        return row;
    }

    private ImageSource RingImage(RingService s)
    {
        float scale = (float)Math.Max(1, VisualTreeHelper.GetDpi(this).DpiScaleX);
        using var bitmap = RingArtwork.RenderRing(s, 64, scale, light: true);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit();
        image.Freeze();
        return image;
    }
}
