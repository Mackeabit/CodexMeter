using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Forms = System.Windows.Forms;

namespace CodexMeter;

internal static class TaskbarLayoutVerification
{
    // Uses synthetic data and only our own temporary child window. No account requests or settings writes.
    internal static int Run(string[] args)
    {
        string folder = Path.GetFullPath(args.SkipWhile(a => a != "--verify-taskbar-layout").Skip(1).First());
        Directory.CreateDirectory(folder);
        var observations = new List<object>();
        IntPtr handle = IntPtr.Zero;
        try
        {
            using (var host = new TaskbarHost(() => { }, () => { }, () => { }))
            {
                var now = DateTimeOffset.Now;
                var week = new Quota("codex", "Codex", 4, 10080, now.AddDays(1));
                var first = new Quota("codex", "Codex", 63, 300, now.AddHours(2));
                foreach (var rows in new[] { new[] { week }, new[] { first, week } })
                {
                    var snapshot = new Snapshot(rows, "fixture", null, true, null, now);
                    var timer = Stopwatch.StartNew();
                    do
                    {
                        host.Update(snapshot, false);
                        Forms.Application.DoEvents();
                        if (host.Attached) break;
                        Thread.Sleep(80);
                    } while (timer.Elapsed < TimeSpan.FromSeconds(5));
                    if (!host.Attached) throw new InvalidOperationException(host.Status);
                    handle = host.Handle;
                    TaskbarNative.GetWindowRect(handle, out var rect);
                    var parent = TaskbarNative.GetParent(handle);
                    float scale = TaskbarNative.GetDpiForWindow(parent) / 96f;
                    bool hit = TaskbarNative.WindowFromPoint(new Point(rect.Left + rect.Bounds.Width / 2, rect.Top + rect.Bounds.Height / 2)) == handle;
                    if (!hit) throw new InvalidOperationException("Meter is not reachable on the taskbar");
                    if (rect.Bounds.Width / scale + .01f < MinimumWidth) throw new InvalidOperationException("Reset column width was not reserved");
                    observations.Add(new { rows = rows.Length, width = rect.Bounds.Width, height = rect.Bounds.Height, dpi = scale * 96, resetWidthReserved = true, reachable = hit, status = host.Status });
                }
            }
            if (TaskbarNative.IsWindow(handle)) throw new InvalidOperationException("Temporary taskbar window was not removed");
            File.WriteAllText(Path.Combine(folder, "taskbar-layout.json"), JsonSerializer.Serialize(new { passed = true, version = Installation.Version, mode = "actual-taskbar-synthetic-data", temporaryWindowRemoved = true, observations }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(folder, "taskbar-layout.json"), JsonSerializer.Serialize(new { passed = false, observations, error = e.Message }));
            return 1;
        }
    }
    private const int MinimumWidth = 240;
}
