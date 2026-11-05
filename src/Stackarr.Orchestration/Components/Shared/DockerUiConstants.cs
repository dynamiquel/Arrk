namespace Stackarr.Orchestration.Components.Shared;

/// <summary>
/// Docker/container UI constants (thresholds, limits, refresh intervals, label keys, id
/// truncation). Docker-specific so it stays with the Docker pages rather than the shared UI
/// vocabulary in <see cref="Stackarr.Ui.Components.UiConstants"/>.
/// </summary>
public static class DockerUiConstants
{
    // Volume usage threshold (percentage)
    public const double VolumeWarningThreshold = 75.0;

    // File preview limits
    public const int MaxTextPreviewBytes = 1_000_000;
    public const int MaxTextPreviewChars = 50_000;
    public const int MaxBinaryPreviewBytes = 500_000;
    public const int HexDumpPreviewBytes = 512;

    // Container logs
    public const int DefaultLogTailLines = 200;

    // Timer intervals (seconds)
    public const int DashboardInitialDelaySeconds = 1;
    public const int DashboardRefreshSeconds = 2;
    public const int ServiceViewInitialDelaySeconds = 5;
    public const int ServiceViewRefreshSeconds = 5;
    public const int InfrastructureInitialDelaySeconds = 2;
    public const int InfrastructureRefreshSeconds = 5;
    public const int ContainerDetailInitialDelaySeconds = 2;
    public const int ContainerDetailRefreshSeconds = 5;
    public const int VolumeDetailInitialDelaySeconds = 10;
    public const int VolumeDetailRefreshSeconds = 30;

    // Docker label keys
    public const string LabelService = "stackarr.service";
    public const string LabelManaged = "stackarr.managed";
    public const string LabelManagedValue = "true";
    public const string ManagedNetworkPrefix = "stackarr-";

    // Container ID display truncation length
    public const int ContainerIdDisplayLength = 12;
}
