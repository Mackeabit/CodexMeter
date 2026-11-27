using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CodexMeter;

/// <summary>
/// Low-usage notices waiting for Windows to accept notifications. One entry per service (Codex, Claude): a newer notice
/// for the same service replaces the older one, so a held 20% warning followed by a 5% warning shows only the 5% one.
/// Entries older than <see cref="MaxAge"/> are dropped; by then the meter itself has long shown the state.
/// </summary>
internal sealed class NoticeQueue
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);
    private readonly List<(UsageNotification Notice, DateTimeOffset Added)> items = new();
    public int Count => items.Count;

    public void Add(UsageNotification notice, DateTimeOffset now)
    {
        items.RemoveAll(x => x.Notice.Service == notice.Service);
        items.Add((notice, now));
    }

    /// <summary>The oldest notice still worth showing, removed from the queue; null when nothing is waiting.</summary>
    public UsageNotification? Next(DateTimeOffset now)
    {
        items.RemoveAll(x => now - x.Added > MaxAge);
        if (items.Count == 0) return null;
        var first = items[0];
        items.RemoveAt(0);
        return first.Notice;
    }

    // QUNS_ACCEPTS_NOTIFICATIONS = 5. Observed on Windows 11 with Do Not Disturb on: 2 (QUNS_BUSY).
    // Not present (1), busy (2), full-screen D3D (3), presentation (4), quiet time (6) and immersive app (7) all hold.
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    public static bool WindowsAcceptsNotifications()
    {
        try { return SHQueryUserNotificationState(out int state) != 0 || state == 5; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return true; }
    }
}
