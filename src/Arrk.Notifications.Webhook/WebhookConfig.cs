using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using FluentValidation;

namespace Arrk.Notifications.Webhook;

public class WebhookConfig
{
    public const string SectionName = "notifications:webhook";

    [Description("Enable generic JSON webhook notifications")]
    public bool Enabled { get; set; } = false;

    [Description("URL that receives a JSON POST for each notification")]
    public string Url { get; set; } = "";

    [Description("Optional raw Authorization header value, e.g. 'Bearer abc123'")]
    [DataType(DataType.Password)]
    public string? AuthHeader { get; set; }

    [Description("Per-topic webhook URLs. '#'/'*' match any depth and '+' matches one level; the most specific match wins. Blank values fall back to the default URL.")]
    public Dictionary<string, string> Routing { get; set; } = new();

    [Description("Named role to webhook URL, e.g. Admin. Role alerts also reach these endpoints; unmapped roles are not sent to the global webhook. Configure access control at the destination yourself if privacy is required.")]
    public Dictionary<string, string> RoleRouting { get; set; } = new();

    [Description("Topic patterns never delivered to this webhook. Matching alerts still appear in the in-app inbox.")]
    public List<string> Exclude { get; set; } = [];
}

public sealed class WebhookConfigValidator : AbstractValidator<WebhookConfig>
{
    public WebhookConfigValidator()
    {
        RuleFor(x => x.Url).Must(IsBlankOrHttpUrl)
            .WithMessage("Webhook URL must be an absolute http(s) URL.");
        RuleForEach(x => x.Routing).Must(entry => IsBlankOrHttpUrl(entry.Value))
            .WithMessage("Webhook per-topic URLs must be absolute http(s) URLs.");
        RuleForEach(x => x.RoleRouting).Must(entry => IsBlankOrHttpUrl(entry.Value))
            .WithMessage("Role webhook URLs must be absolute http(s) URLs.");
    }

    static bool IsBlankOrHttpUrl(string? value) => string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https");
}
