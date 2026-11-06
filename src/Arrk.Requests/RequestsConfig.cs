using System.ComponentModel;

namespace Arrk.Requests;

/// <summary>
/// Configuration for the user request system (a Seerr-style request/approval flow).
/// </summary>
public class RequestsConfig
{
    [Description("Enable the request system. When disabled, users cannot create requests and the Requests page is hidden.")]
    public bool Enabled { get; set; } = true;

    [Description("Allow users to request a whole TV show")]
    public bool AllowSeriesRequests { get; set; } = true;

    [Description("Allow users to request a single season of a TV show")]
    public bool AllowSeasonRequests { get; set; } = true;

    [Description("Allow users to request a single episode of a TV show")]
    public bool AllowEpisodeRequests { get; set; } = true;

    [Description("Default quality/profile name used when a request does not specify one (blank = backend default)")]
    public string DefaultQualityProfile { get; set; } = "";

    [Description("Maximum number of open (pending) requests a single user may have at once (0 = unlimited)")]
    public int MaxOpenPerUser { get; set; }

    [Description("When a request is denied, also blacklist the title so it cannot be requested again")]
    public bool BlacklistOnDeny { get; set; }

    [Description("Requests from these accounts are approved automatically (no admin review)")]
    public List<string> AutoApproveUsers { get; set; } = [];

    [Description("Requests from accounts with these roles are approved automatically (e.g. Admin). Admins normally add media directly rather than request.")]
    public List<string> AutoApproveRoles { get; set; } = [];
}
