using FluentValidation;

namespace Stackarr.Ai;

/// <summary>
/// Validates the <c>ai</c> section. Nothing here is enforced while AI is disabled, so an empty/absent
/// section stays valid and the app keeps running without AI.
/// </summary>
public class AiConfigValidator : AbstractValidator<AiConfig>
{
    public AiConfigValidator()
    {
        RuleFor(x => x.TimeoutSeconds).InclusiveBetween(1, 3600);
        RuleFor(x => x.Features.ReleaseRanking.Mode).Must(AiRankingModes.IsKnown)
            .WithMessage($"Release ranking mode must be one of: {string.Join(", ", AiRankingModes.All)}");
        RuleFor(x => x.Features.ReleaseRanking.MaxCandidates).InclusiveBetween(1, 50);
        RuleFor(x => x.Features.ReleaseRanking.MinConfidence).InclusiveBetween(0, 1);
        RuleFor(x => x.Features.ReleaseRanking.AmbiguityScoreWindow).InclusiveBetween(0, 100);
        RuleFor(x => x.Features.ImportMatching.Mode).Must(AiRankingModes.IsKnown)
            .WithMessage($"Import matching mode must be one of: {string.Join(", ", AiRankingModes.All)}");
        RuleFor(x => x.Features.ImportMatching.MaxCandidates).InclusiveBetween(1, 50);
        RuleFor(x => x.Features.ImportMatching.MinConfidence).InclusiveBetween(0, 1);
        RuleFor(x => x.Features.ImportMatching.AmbiguityScoreWindow).InclusiveBetween(0, 100);
        RuleFor(x => x.Features.Recommendations.PicksPerUser).InclusiveBetween(1, 50);
        RuleFor(x => x.Features.Recommendations.RefreshHours).InclusiveBetween(1, 720);
        RuleFor(x => x.Limits.MaxRequestsPerDay).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Limits.MaxTokensPerMonth).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Limits.CacheMinutes).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Features.Assistant.MaxToolRounds).InclusiveBetween(1, 4);
        RuleFor(x => x.Personas).Must(HaveValidPersonas)
            .WithMessage("Persona keys must be unique (built-ins: plain, jarvis, cortana, johnson, emilia, coconut), custom personas need a key/name/style, and the default must exist.");

        When(x => x.Enabled, () =>
        {
            RuleFor(x => x.Provider).Must(p => AiProviders.IsKnown(p))
                .WithMessage($"Provider must be one of: {string.Join(", ", AiProviders.All)}");

            RuleFor(x => x).Must(x => !string.IsNullOrWhiteSpace(x.ResolveChatModel()))
                .WithMessage("A chat model is required when AI is enabled (models.chat)");

            RuleFor(x => x.BaseUrl).Must(BeBlankOrAbsoluteHttpUrl)
                .WithMessage("Base URL must be an absolute http(s) URL");

            When(x => string.Equals(x.Provider, AiProviders.Custom, StringComparison.OrdinalIgnoreCase), () =>
            {
                RuleFor(x => x.BaseUrl).NotEmpty()
                    .WithMessage("A base URL is required for the 'custom' provider");
            });

            When(x => AiProviders.Find(x.Provider)?.RequiresKey == true, () =>
            {
                RuleFor(x => x.ApiKey).NotEmpty()
                    .WithMessage("An API key is required for this provider");
            });
        });
    }

    static bool HaveValidPersonas(AiPersonasConfig personas)
    {
        HashSet<string> keys = new(AiPersonas.BuiltIn.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
        foreach (AiPersonaDefinition custom in personas.Custom)
        {
            string key = (custom.Key ?? "").Trim();
            if (key.Length == 0 || !keys.Add(key))
                return false;
            if (string.IsNullOrWhiteSpace(custom.Name) || string.IsNullOrWhiteSpace(custom.Style))
                return false;
            if (custom.Name.Length > 64 || custom.Style.Length > 2000)
                return false;
        }

        string defaultKey = string.IsNullOrWhiteSpace(personas.Default)
            ? AiPersonas.Plain
            : personas.Default.Trim();
        return keys.Contains(defaultKey);
    }

    static bool BeBlankOrAbsoluteHttpUrl(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
}
