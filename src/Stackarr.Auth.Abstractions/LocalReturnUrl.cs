namespace Stackarr.Auth.Abstractions;

/// <summary>
/// Validates post-login redirect targets. Only a local absolute path is allowed: anything that a
/// browser could interpret as another origin (a scheme, a protocol-relative <c>//host</c>, a
/// backslash-normalised <c>/\host</c>, or an encoded variant) falls back to the supplied default.
/// </summary>
public static class LocalReturnUrl
{
    public const string Default = "/";

    public static string Normalize(string? returnUrl, string fallback = Default)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return fallback;

        string value = returnUrl.Trim();
        if (value.Length == 0 || value[0] != '/')
            return fallback;

        // Browsers treat backslashes like slashes in URLs, so `/\evil` and `/%5Cevil` would become
        // protocol-relative redirects. Control characters can also hide a second line/authority.
        if (value.Contains('\\') || value.Any(char.IsControl))
            return fallback;

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return fallback;
        }

        if (decoded.Contains('\\') || decoded.Any(char.IsControl))
            return fallback;

        // `//host` and `///host` are protocol-relative; after decoding `%2F` they must be caught
        // too. A single leading slash followed by a normal path segment is local.
        if (decoded.StartsWith("//", StringComparison.Ordinal))
            return fallback;

        if (!Uri.TryCreate(value, UriKind.Relative, out Uri? relative) || relative.IsAbsoluteUri)
            return fallback;

        return value;
    }

    /// <summary>True when the value is safe to send back to the browser as a local redirect.</summary>
    public static bool IsLocal(string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl)
            && string.Equals(Normalize(returnUrl, fallback: "\0"), returnUrl.Trim(), StringComparison.Ordinal);
}
