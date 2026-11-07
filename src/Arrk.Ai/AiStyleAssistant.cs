using System.Text.RegularExpressions;
using Arrk.Ui;

namespace Arrk.Ai;

/// <summary>
/// Turns a plain-language brief into per-account CSS. AI is advisory: the user reviews the
/// generated rules and saves them from the Account page. The result is plain CSS only; the editor
/// sanitises and stores it, and the shell applies it after the shared theme.
/// </summary>
public sealed partial class AiStyleAssistant(AiService ai) : IUserCssAssistant
{
    public bool IsAvailable => ai.Enabled && ai.Config.Features.Style.Enabled;

    public async Task<string?> GenerateAsync(UserCssRequest request, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(request.Instructions))
            return null;

        string prompt = BuildPrompt(request);
        string? answer = await ai.AskAsync(
            prompt,
            AiModelRole.Medium,
            AiFeatureKeys.Style,
            "css",
            request.UserId,
            ct);

        return Clean(answer);
    }

    internal static string BuildPrompt(UserCssRequest request)
    {
        string current = string.IsNullOrWhiteSpace(request.CurrentCss)
            ? "(none yet)"
            : request.CurrentCss.Trim();

        return $$"""
            You write custom CSS overrides for Arrk, a self-hosted media dashboard.

            {{CssThemeGuide.Text}}

            The user's request:
            {{request.Instructions.Trim()}}

            Existing custom CSS to keep or refine (do not repeat verbatim unless unchanged):
            {{current}}
            """;
    }

    /// <summary>Strips code fences and a stray leading language label from a model response.</summary>
    public static string? Clean(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;

        string text = answer.Trim();
        Match fence = FencedBlock().Match(text);
        if (fence.Success)
            text = fence.Groups["css"].Value;

        text = text.Trim();
        if (text.StartsWith("css:", StringComparison.OrdinalIgnoreCase))
            text = text[4..].TrimStart();
        else if (text.StartsWith("css\n", StringComparison.OrdinalIgnoreCase))
            text = text[4..].TrimStart();

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [GeneratedRegex("```(?:css)?\\s*(?<css>.*?)```", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FencedBlock();
}
