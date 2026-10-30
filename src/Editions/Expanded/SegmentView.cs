using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace CodexMeter;

internal static class SegmentView
{
    private static Brush Color(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
    internal static Grid CreateRow(Quota? quota, bool single, bool stale, bool denied, DateTimeOffset now)
    {
        bool warning = stale || denied || quota?.ResetPending(now) == true;
        string digits = warning ? "!" : quota == null ? "—" : Math.Floor(quota.Remaining).ToString("0");
        var row = new Grid { Height = single ? 44 : 22, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(single ? digits.Length > 2 ? 68 : 57 : 43) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
        row.ToolTip = UsageHover.Describe(quota, stale, denied, now) + "\nPC 현지시간 · 끌어서 이동 · 두 번 클릭하면 상세";
        ToolTipService.SetInitialShowDelay(row, 400);
        ToolTipService.SetShowDuration(row, 30000);
        System.Windows.Automation.AutomationProperties.SetName(row, UsageHover.Describe(quota, stale, denied, now));
        var cells = new UniformGrid { Rows = 1, Columns = 10, Height = single ? 24 : 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        for (int i = 0; i < 10; i++)
        {
            double fraction = quota == null ? 0 : Math.Clamp(quota.Remaining / 10 - i, 0, 1);
            var cell = new Grid { Margin = new Thickness(0, 0, i == 9 ? 0 : single ? 3 : 2, 0) };
            var radius = new CornerRadius(single ? 3 : 2);
            cell.Children.Add(new Border { Background = Color("#424242"), CornerRadius = radius });
            var fill = new Border { Background = Color(warning ? "#808080" : "#DE805E"), CornerRadius = radius };
            cell.SizeChanged += (_, _) => fill.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, cell.ActualWidth * fraction), Math.Max(0, cell.ActualHeight)));
            cell.Children.Add(fill); cells.Children.Add(cell);
        }
        row.Children.Add(cells);
        var number = new TextBlock { Tag = "remaining", FontFamily = new FontFamily("Segoe UI"), FontSize = single ? 30 : 17, FontWeight = FontWeights.Normal, Foreground = Color("#F0F0F0"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        number.Inlines.Add(new Run(digits));
        if (!warning && quota != null) number.Inlines.Add(new Run("%") { FontSize = single ? 17 : 12, Foreground = Color("#BCBCBC") });
        Grid.SetColumn(number, 1); row.Children.Add(number);
        var reset = MeterArtwork.ResetParts(quota);
        var group = new StackPanel { Orientation = single ? Orientation.Vertical : Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var date = new TextBlock { Text = reset.Date, FontSize = 12, FontWeight = FontWeights.Normal, Foreground = Color("#BCBCBC"), VerticalAlignment = VerticalAlignment.Center };
        if (single && quota?.Reset != null) date.Inlines.Add(new Run("  리셋") { FontSize = 11 });
        group.Children.Add(date);
        group.Children.Add(new TextBlock { Text = reset.Time, FontSize = single ? 19 : 14, FontWeight = FontWeights.Normal, Foreground = Color("#F0F0F0"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(single ? 0 : 6, 0, 0, 0) });
        if (!single && quota?.Reset == null)
        {
            group.Children.Clear();
            group.Children.Add(new TextBlock { Text = "시간 미제공", FontSize = 11, Foreground = Color("#BCBCBC"), VerticalAlignment = VerticalAlignment.Center });
        }
        var resetArea = new Border { BorderBrush = Color("#515151"), BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(12, 0, 0, 0), Margin = new Thickness(0, single ? 3 : 0, 0, single ? 3 : 0), Child = group };
        Grid.SetColumn(resetArea, 3); row.Children.Add(resetArea);
        return row;
    }
}
