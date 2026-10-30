using System;
using System.Windows;
using System.Windows.Threading;

namespace CodexMeter;

public partial class MainWindow
{
    internal void OnDisplayChanged(object? sender, EventArgs e)
    {
        // Let pending DPI/layout work settle, then keep the current position whenever
        // it still fits. KeepVisible also works while the already-loaded window is hidden.
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => {
            if (exiting) return;
            var before = new Point(Left, Top);
            KeepVisible();
            if (Left != before.X || Top != before.Y) Save();
        });
    }
}
