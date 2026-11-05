using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Stackarr.Core.Configuration;

namespace Stackarr.Core.Notifications;

/// <summary>
/// Identifies the Stackarr instance in external notifications: the stack name and the public URL
/// users should open. Both are read live so config hot-reload applies immediately.
/// </summary>
public sealed class NotificationStackInfo(
    IConfiguration configuration,
    IOptionsMonitor<RoutesConfig> routes)
{
    public string Name
    {
        get
        {
            string? configured = configuration["StackName"];
            return string.IsNullOrWhiteSpace(configured) ? "Stackarr" : configured.Trim();
        }
    }

    public string PublicUrl => routes.CurrentValue.ResolvePublicBaseUrl();

    /// <summary>Turns a relative in-app link into an absolute URL users can open.</summary>
    public string? AbsoluteUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        string path = url.StartsWith('/') ? url : "/" + url;
        return PublicUrl + path;
    }
}
