using System.Text.RegularExpressions;
using Arrk.Core.Services;

namespace Arrk.Ui;

/// <summary>A request to the optional AI helper that writes custom CSS from a plain-language brief.</summary>
public sealed record UserCssRequest(int UserId, string Instructions, string CurrentCss);

/// <summary>
/// The seam between the always-available UI layer and the optional AI module. The AI module
/// implements this and registers it in DI; with AI off no implementation reports available and the
/// editor simply hides the generate action.
/// </summary>
public interface IUserCssAssistant
{
    bool IsAvailable { get; }

    Task<string?> GenerateAsync(UserCssRequest request, CancellationToken ct = default);
}

/// <summary>
/// Stores and applies an account's custom CSS. CSS is additive: it is rendered after the shared
/// theme so it can override tokens and component rules without forking the UI. This is deliberately
/// not a palette system — the user (or the AI assistant) supplies the rules.
/// </summary>
public sealed partial class UserCssService(
    UserUiPreferenceService preferences,
    IEnumerable<IUserCssAssistant> assistants)
{
    public const int MaxCssLength = 65_536;

    /// <summary>Raised when an account's CSS changes, so a live shell can update without a reload.</summary>
    public event Action<int>? Changed;

    /// <summary>Whether an AI helper is configured and enabled right now.</summary>
    public bool CanGenerate => assistants.Any(a => a.IsAvailable);

    public async Task<string> GetAsync(int userId, CancellationToken ct = default)
    {
        UserUiPreference? preference = await preferences.GetAsync(userId, ct);
        return preference?.CustomCss ?? "";
    }

    /// <summary>Stores CSS after removing document-breaking sequences.</summary>
    public async Task SetAsync(int userId, string css, CancellationToken ct = default)
    {
        string sanitized = Sanitize(css);
        if (sanitized.Length > MaxCssLength)
            throw new InvalidOperationException($"Custom CSS is limited to {MaxCssLength} characters.");

        await preferences.SaveCssAsync(userId, sanitized, ct);
        Changed?.Invoke(userId);
    }

    public async Task ClearAsync(int userId, CancellationToken ct = default)
    {
        await preferences.ClearCssAsync(userId, ct);
        Changed?.Invoke(userId);
    }

    /// <summary>
    /// Asks the configured AI helper for CSS. Returns null when no helper is available or the model
    /// returns nothing usable; the caller keeps the existing CSS in that case.
    /// </summary>
    public async Task<string?> GenerateAsync(int userId, string instructions, string currentCss, CancellationToken ct = default)
    {
        IUserCssAssistant? assistant = assistants.FirstOrDefault(a => a.IsAvailable);
        if (assistant is null || string.IsNullOrWhiteSpace(instructions))
            return null;

        string? generated = await assistant.GenerateAsync(
            new UserCssRequest(userId, instructions.Trim(), Sanitize(currentCss)), ct);
        return string.IsNullOrWhiteSpace(generated) ? null : Sanitize(generated);
    }

    /// <summary>
    /// Removes the only sequence that could escape the style element, plus NULs. CSS syntax itself
    /// (including <c>&gt;</c> combinators) is left untouched.
    /// </summary>
    public static string Sanitize(string? css)
    {
        if (string.IsNullOrWhiteSpace(css))
            return "";

        string cleaned = StyleCloseTag().Replace(css, "");
        return cleaned.Replace("\0", "").Trim();
    }

    [GeneratedRegex("</\\s*style", RegexOptions.IgnoreCase)]
    private static partial Regex StyleCloseTag();
}
