using System.ComponentModel;
using Arrk.Auth.Abstractions;
using FluentValidation;

namespace Arrk.Notifications;

/// <summary>
/// One shared-endpoint subscription. Matching rules are additive, including duplicate topics and URLs.
/// </summary>
public sealed class NotificationRoute
{
    [Description("Event topic/prefix. '+' matches one level; '#' or '*' matches any depth. Every matching route sends.")]
    public string Topic { get; set; } = "#";

    [Description("Webhook URL for this destination. Configure access control at the destination if privacy is required.")]
    public string Url { get; set; } = "";

    [Description("Source audience roles accepted by this route, not recipient permissions. Defaults to User (public events); use Admin for admin-targeted events.")]
    public List<string> FromRoles { get; set; } = [ArrkRoles.User];
}

public sealed class NotificationRouteValidator : AbstractValidator<NotificationRoute>
{
    public NotificationRouteValidator()
    {
        RuleFor(x => x.Topic).NotEmpty();
        RuleFor(x => x.Url).Must(url => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && uri.Scheme is "http" or "https").WithMessage("Route URL must be an absolute http(s) URL.");
        RuleFor(x => x.FromRoles).NotEmpty();
        RuleForEach(x => x.FromRoles).NotEmpty();
    }
}

/// <summary>Matches both topic and source role; individual accounts never match shared routes.</summary>
public static class WebhookRouting
{
    public static IReadOnlyList<string> Resolve(
        IReadOnlyList<NotificationRoute> routing, NotificationRecipient recipient, string topic)
    {
        if (recipient.UserId is not null || recipient.Role is null)
            return [];

        return [.. routing.Where(route => !string.IsNullOrWhiteSpace(route.Topic)
            && !string.IsNullOrWhiteSpace(route.Url)
            && route.FromRoles.Contains(recipient.Role, StringComparer.OrdinalIgnoreCase)
            && TopicFilter.Matches(route.Topic, topic)).Select(route => route.Url.Trim())];
    }
}
