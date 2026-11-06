using MudBlazor;
using Arrk.Core.Enums;
using Arrk.Ui.Components;

namespace Arrk.Orchestration.Components.Shared;

/// <summary>
/// Maps data states to CSS class names and MudBlazor colors.
/// Used by multiple components to render status dots, usage bars, and volume indicators.
/// </summary>
public static class CssClassHelper
{
    /// <summary>
    /// Returns the CSS class for a hardware usage bar based on percentage thresholds.
    /// </summary>
    public static string GetUsageClass(double percent) => percent switch
    {
        >= UiConstants.UsageHighThreshold => UiConstants.UsageBarHigh,
        >= UiConstants.UsageMidThreshold => UiConstants.UsageBarMid,
        _ => UiConstants.UsageBarLow
    };

    /// <summary>
    /// Returns the CSS class for a container status dot.
    /// </summary>
    public static string GetContainerDotClass(ContainerState state) => state switch
    {
        ContainerState.Running => UiConstants.StatusDotRunning,
        ContainerState.Starting => UiConstants.StatusDotStarting,
        ContainerState.Exited => UiConstants.StatusDotStopped,
        ContainerState.Stopped => UiConstants.StatusDotStopped,
        ContainerState.Paused => UiConstants.StatusDotPaused,
        ContainerState.Restarting => UiConstants.StatusDotStarting,
        ContainerState.Removing => UiConstants.StatusDotUnknown,
        ContainerState.Dead => UiConstants.StatusDotUnknown,
        ContainerState.Created => UiConstants.StatusDotUnknown,
        _ => UiConstants.StatusDotUnknown
    };

    /// <summary>
    /// Returns the CSS class for a container status text label.
    /// </summary>
    public static string GetContainerStatusTextClass(ContainerState state) => state switch
    {
        ContainerState.Running => "st-status-text--running",
        ContainerState.Starting => "st-status-text--starting",
        ContainerState.Exited => "st-status-text--stopped",
        ContainerState.Stopped => "st-status-text--stopped",
        ContainerState.Paused => "st-status-text--paused",
        ContainerState.Restarting => "st-status-text--starting",
        _ => "st-status-text--muted"
    };

    /// <summary>
    /// Returns a MudBlazor Color for a volume usage percentage.
    /// </summary>
    public static Color GetVolumeColor(double percent) => percent switch
    {
        >= UiConstants.UsageHighThreshold => Color.Error,
        >= DockerUiConstants.VolumeWarningThreshold => Color.Warning,
        _ => Color.Success
    };

    /// <summary>
    /// Gets a label value from a Docker labels dictionary, or null if not present.
    /// </summary>
    public static string? GetLabel(IDictionary<string, string>? labels, string key)
    {
        if (labels == null)
        {
            return null;
        }

        labels.TryGetValue(key, out string? value);
        return value;
    }

    /// <summary>
    /// Determines if a Docker network is managed by Arrk based on labels or name prefix.
    /// </summary>
    public static bool IsManagedNetwork(IDictionary<string, string>? labels, string? name)
    {
        bool hasManagedLabel = labels != null
            && labels.TryGetValue(DockerUiConstants.LabelManaged, out string? v)
            && v == DockerUiConstants.LabelManagedValue;
        bool hasManagedPrefix = name?.StartsWith(DockerUiConstants.ManagedNetworkPrefix) == true;
        return hasManagedLabel || hasManagedPrefix;
    }
}
