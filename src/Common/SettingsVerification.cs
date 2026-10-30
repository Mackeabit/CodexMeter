using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace CodexMeter;

internal static class SettingsVerification
{
    internal static void Run(MainWindow window, Action<bool, string> check)
    {
        foreach (var (compact, segmented, expected) in new[] {
            (false, false, WindowView.Detail), (true, false, WindowView.Compact),
            (false, true, WindowView.Segmented), (true, true, WindowView.Segmented) })
        {
            var old = new Preferences { Compact = compact, Segmented = segmented, Left = 920, Top = 264,
                Taskbar = false, Topmost = false, Opacity = .8, IntervalSeconds = 300, Alerts = true,
                CodexPath = @"C:\사용자 폴더\codex.exe" };
            var loaded = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(old))!;
            loaded.Normalize();
            check(loaded.View == expected && !(loaded.Compact && loaded.Segmented),
                $"legacy view {compact}/{segmented} keeps the previously visible layout with one selection");
            check(loaded.Left == old.Left && loaded.Top == old.Top && loaded.Taskbar == old.Taskbar &&
                loaded.Topmost == old.Topmost && loaded.Opacity == old.Opacity && loaded.IntervalSeconds == old.IntervalSeconds &&
                loaded.Alerts == old.Alerts && loaded.CodexPath == old.CodexPath,
                $"legacy view {compact}/{segmented} preserves all unrelated preferences");
            foreach (var view in Enum.GetValues<WindowView>()) {
                loaded.SetView(view);
                var roundTrip = JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(loaded))!;
                check(roundTrip.View == view && !(roundTrip.Compact && roundTrip.Segmented),
                    $"legacy view {compact}/{segmented} switches and persists exclusively as {view}");
            }
        }
        var reversed = JsonSerializer.Deserialize<Preferences>("{\"Segmented\":true,\"Compact\":true,\"TrayStyle\":\"battery\"}")!;
        reversed.Normalize();
        check(reversed.View == WindowView.Segmented && !reversed.Compact, "legacy conflicts resolve independently of JSON key order or removed icon settings");

        MenuItem Find(IEnumerable<MenuItem> items, string title) => items.Single(item => (string)item.Header == title);
        Forms.ToolStripMenuItem FindTray(IEnumerable<Forms.ToolStripMenuItem> items, string title) => items.Single(item => item.Text == title);
        MenuItem WindowGroup(ContextMenu menu) => Find(menu.Items.Cast<MenuItem>(), "별도 창 설정");
        MenuItem Views(ContextMenu menu) => Find(WindowGroup(menu).Items.Cast<MenuItem>(), "표시 방식");
        bool TaskbarEnabled() => Find(Find(window.BuildSettingsMenu().Items.Cast<MenuItem>(), "작업표시줄 설정").Items.Cast<MenuItem>(), "분할 막대 표시").IsChecked;
        bool taskbarBefore = TaskbarEnabled();
        foreach (var (title, expected) in new[] { ("작게", WindowView.Compact), ("막대", WindowView.Segmented), ("상세", WindowView.Detail) })
        {
            using var trayMenu = window.BuildTrayMenu();
            var trayViews = FindTray(FindTray(trayMenu.Items.Cast<Forms.ToolStripMenuItem>(), "별도 창 설정").DropDownItems.Cast<Forms.ToolStripMenuItem>(), "표시 방식");
            FindTray(trayViews.DropDownItems.Cast<Forms.ToolStripMenuItem>(), title).PerformClick();
            bool visible = expected switch {
                WindowView.Compact => window.Compact.Visibility == Visibility.Visible,
                WindowView.Segmented => window.SegmentRows.Visibility == Visibility.Visible,
                _ => window.Expanded.Visibility == Visibility.Visible
            };
            var choices = Views(window.BuildSettingsMenu()).Items.Cast<MenuItem>().ToArray();
            check(visible && choices.Count(item => item.IsChecked) == 1 && Find(choices, title).IsChecked,
                $"tray selection {title} updates the visible layout and sole checked window-menu item");
            // Selecting the already checked entry must not turn every mode off.
            Find(choices, title).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            check(Views(window.BuildSettingsMenu()).Items.Cast<MenuItem>().Count(item => item.IsChecked) == 1 && TaskbarEnabled() == taskbarBefore,
                $"reselecting {title} retains one view and leaves taskbar visibility independent");
        }

        var settings = window.BuildSettingsMenu();
        var floating = WindowGroup(settings).Items.Cast<MenuItem>().Select(item => (string)item.Header).ToArray();
        var taskbar = Find(settings.Items.Cast<MenuItem>(), "작업표시줄 설정").Items.Cast<MenuItem>().Select(item => (string)item.Header).ToArray();
        check(new[] { "표시 방식", "항상 위에 표시", "불투명도", "위치 초기화" }.All(floating.Contains) &&
            !taskbar.Any(floating.Contains), "floating-window controls are grouped separately from taskbar controls");
        MenuItem Notifications() => Find(window.BuildSettingsMenu().Items.Cast<MenuItem>(), "알림 설정");
        check(Notifications().Items.Count == 1, "notification pause is hidden when alerts are off");
        ((MenuItem)Notifications().Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(Notifications().Items.Count == 2, "enabling alerts exposes the per-reset pause option");
        ((MenuItem)Notifications().Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(Notifications().Items.Count == 1, "disabling alerts hides pause again");

        IEnumerable<string> WindowTree(IEnumerable<MenuItem> items, string parent = "") {
            foreach (var item in items) {
                string path = parent + "/" + item.Header;
                yield return $"{path}:{item.IsChecked}:{item.IsEnabled}";
                foreach (string child in WindowTree(item.Items.Cast<MenuItem>(), path)) yield return child;
            }
        }
        IEnumerable<string> TrayTree(IEnumerable<Forms.ToolStripItem> items, string parent = "") {
            foreach (var item in items) {
                if (item is Forms.ToolStripSeparator) continue;
                string path = parent + "/" + item.Text;
                yield return $"{path}:{item is Forms.ToolStripMenuItem option && option.Checked}:{item.Enabled}";
                if (item is Forms.ToolStripMenuItem submenu)
                    foreach (string child in TrayTree(submenu.DropDownItems.Cast<Forms.ToolStripItem>(), path)) yield return child;
            }
        }
        using var finalTray = window.BuildTrayMenu();
        check(WindowTree(window.BuildSettingsMenu().Items.Cast<MenuItem>()).SequenceEqual(TrayTree(finalTray.Items.Cast<Forms.ToolStripItem>())),
            "tray and floating-window menus expose identical groups, selections and enabled states");
    }
}
