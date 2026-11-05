using Stackarr.Core.Enums;

namespace Stackarr.Requests.Models;

/// <summary>
/// A title/scope that has been blacklisted, so future requests for it are refused. Matching is by
/// domain plus any overlapping provider id, so it works regardless of which id a user supplied.
/// </summary>
public class RequestBlacklist
{
    public long Id { get; set; }

    public MediaDomain Domain { get; set; }

    /// <summary>JSON dictionary of provider ids for the blacklisted title.</summary>
    public string ProviderIds { get; set; } = "{}";

    public string Title { get; set; } = "";

    public string? Reason { get; set; }

    public int BlacklistedByUserId { get; set; }
    public DateTime BlacklistedAt { get; set; }
}
