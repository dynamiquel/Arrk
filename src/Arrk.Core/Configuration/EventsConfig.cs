using System.ComponentModel;

namespace Arrk.Core.Configuration;

/// <summary>Settings for the durable event log.</summary>
public class EventsConfig
{
    [Description("Audit log retention")]
    public EventRetentionConfig Retention { get; set; } = new();
}
