using System.ComponentModel;

namespace Arrk.Notifications;

/// <summary>Transport-independent notification and inbox preferences.</summary>
public class NotificationConfig
{
    [Description("Days to keep notifications in the in-app inbox (0 = keep forever)")]
    public int RetentionDays { get; set; } = 90;

    [Description("Show running background jobs in the activity centre and inbox")]
    public bool ShowJobActivity { get; set; }

    [Description("Enable/disable dotted notification topic patterns. '+' matches one level; '#' or '*' matches any depth. The most specific pattern wins; unset topics use their catalogue default.")]
    public Dictionary<string, bool> Topics { get; set; } = new();
}
