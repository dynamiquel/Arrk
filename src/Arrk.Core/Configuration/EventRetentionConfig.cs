using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Arrk.Core.Configuration;

/// <summary>
/// Retention for the durable <c>SystemEvents</c> audit log. Events are removed by age, with
/// optional per-topic overrides so noisy topics (background job start/finish, for example) can
/// be kept for less time than the rest of the log.
/// </summary>
public class EventRetentionConfig
{
    [Description("Purge old events from the audit log")]
    public bool Enabled { get; set; } = true;

    [Description("Days to keep events that do not match a topic override (0 = keep forever)")]
    [Range(0, 36500)]
    public int DefaultRetentionDays { get; set; } = 30;

    [Description("Per-topic retention in days; supports #/* (any depth) and + (one level) wildcards. 0 = keep forever. The most specific matching topic wins.")]
    public Dictionary<string, int> TopicRetentionDays { get; set; } = new()
    {
        ["job.#"] = 7
    };
}
