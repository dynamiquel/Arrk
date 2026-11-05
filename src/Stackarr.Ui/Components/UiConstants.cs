namespace Stackarr.Ui.Components;

/// <summary>
/// Shared UI vocabulary: usage-bar thresholds and the CSS class names the shared components emit.
/// Domain/Docker-specific constants live with their own module, not here.
/// </summary>
public static class UiConstants
{
    // Usage bar thresholds (percentage)
    public const double UsageHighThreshold = 90.0;
    public const double UsageMidThreshold = 70.0;

    // Status dot classes
    public const string StatusDotRunning = "status-dot--running";
    public const string StatusDotStopped = "status-dot--stopped";
    public const string StatusDotStarting = "status-dot--starting";
    public const string StatusDotPaused = "status-dot--paused";
    public const string StatusDotUnknown = "status-dot--unknown";

    // Usage bar classes
    public const string UsageBarLow = "st-hw-bar-fill--low";
    public const string UsageBarMid = "st-hw-bar-fill--mid";
    public const string UsageBarHigh = "st-hw-bar-fill--high";
}
