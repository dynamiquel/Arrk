using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Arrk.Notifications;

namespace Arrk.Core.Configuration;

public class ArrkConfig
{
    public const string SectionName = "";

    [Description("Storage paths configuration")]
    public StorageConfig Storage { get; set; } = new();

    [Description("VPN configuration for anonymized traffic")]
    public VpnConfig Vpn { get; set; } = new();

    [Description("Base URL and routing configuration")]
    public RoutesConfig Routes { get; set; } = new();

    [Description("Notification configuration")]
    public NotificationConfig Notifications { get; set; } = new();

    [Description("Monitoring and health-check settings")]
    public MonitoringConfig Monitoring { get; set; } = new();

    [Description("Event log settings")]
    public EventsConfig Events { get; set; } = new();

    [Description("Stop managed containers on application shutdown")]
    [Display(Name = "Stop containers on shutdown")]
    public bool StopContainersOnShutdown { get; set; } = true;

    [Description("Pin container images to their resolved digest for reproducibility")]
    [Display(Name = "Pin images to digest")]
    public bool PinImagesToDigest { get; set; } = true;

    [Description("Unique stack name for isolating this instance. If empty, a random 8-digit ID is generated on first run.")]
    [Display(Name = "Stack name")]
    public string? StackName { get; set; }
}
