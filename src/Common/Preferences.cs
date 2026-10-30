using System;
using System.Text.Json.Serialization;

namespace CodexMeter;

public enum WindowView { Detail, Compact, Segmented }

public sealed class Preferences
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    // Retain the old JSON keys so older editions can still read these settings.
    public bool Compact { get; set; }
    public bool Segmented { get; set; }
    public bool Taskbar { get; set; } = true;
    public string? CodexPath { get; set; }
    public bool Topmost { get; set; } = true;
    public double Opacity { get; set; } = 1;
    public int IntervalSeconds { get; set; } = 120;
    public bool Alerts { get; set; }

    // Older versions rendered bars first when both flags were true.
    [JsonIgnore] public WindowView View => Segmented ? WindowView.Segmented : Compact ? WindowView.Compact : WindowView.Detail;

    public void SetView(WindowView view)
    {
        if (!Enum.IsDefined(view)) throw new ArgumentOutOfRangeException(nameof(view));
        Compact = view == WindowView.Compact;
        Segmented = view == WindowView.Segmented;
    }

    public void Normalize()
    {
        SetView(View);
        IntervalSeconds = IntervalSeconds is 60 or 120 or 300 ? IntervalSeconds : 120;
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .75, 1) : 1;
    }
}
