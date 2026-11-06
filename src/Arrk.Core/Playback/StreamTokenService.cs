using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Arrk.Core.Playback;

/// <summary>
/// Signs and verifies short-lived, self-authenticating stream URLs.
/// </summary>
/// <remarks>
/// AirPlay hands the media URL to the receiver (Apple TV / HomePod), which then fetches the
/// playlist and segments itself, with none of the browser's cookies. A cookie-only stream proxy
/// can therefore never be cast: the receiver gets a 401 and playback never starts, even though the
/// title and artwork still appear in Control Center (that part is the Media Session API, which is
/// local to the device). Embedding a scoped, expiring token in the URL lets the receiver
/// authenticate without a session cookie.
/// </remarks>
public sealed class StreamTokenService
{
    const string KeyFileName = "stream-token.key";
    const char Separator = '.';

    readonly byte[] _key;

    public StreamTokenService(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 16)
            throw new ArgumentException("The stream-token key must be at least 16 bytes.", nameof(key));

        _key = key;
    }

    /// <summary>
    /// Loads the signing key from the config directory, generating and persisting it once so tokens
    /// stay valid across restarts. Falls back to an in-memory key when the directory is not writable.
    /// </summary>
    public static StreamTokenService Load(string? configDirectory)
    {
        string? file = string.IsNullOrWhiteSpace(configDirectory)
            ? null
            : System.IO.Path.Combine(configDirectory, KeyFileName);

        if (file is not null && File.Exists(file))
        {
            try
            {
                byte[] existing = Convert.FromBase64String(File.ReadAllText(file).Trim());
                if (existing.Length >= 16)
                    return new StreamTokenService(existing);
            }
            catch { /* fall through and regenerate */ }
        }

        byte[] generated = RandomNumberGenerator.GetBytes(32);
        if (file is not null)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                File.WriteAllText(file, Convert.ToBase64String(generated));
            }
            catch { /* best effort; the generated key still works for this process */ }
        }

        return new StreamTokenService(generated);
    }

    /// <summary>Creates a token for a <paramref name="scope"/> + <paramref name="subject"/> pair.</summary>
    public string Create(string scope, string subject, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        long expires = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        string payload = string.Join(Separator, scope, subject, expires.ToString(CultureInfo.InvariantCulture));
        string body = Base64Url(Encoding.UTF8.GetBytes(payload));
        return body + Separator + Base64Url(Sign(body));
    }

    /// <summary>
    /// Verifies a token's signature, scope and expiry, returning the subject it was minted for.
    /// The subject is what the caller binds the token to (a playback session id, a username, ...).
    /// </summary>
    public bool TryValidate(string? token, string expectedScope, out string subject, out DateTimeOffset expiresAt)
    {
        subject = "";
        expiresAt = default;

        if (string.IsNullOrEmpty(token))
            return false;

        int separator = token.IndexOf(Separator, StringComparison.Ordinal);
        if (separator <= 0 || separator == token.Length - 1)
            return false;

        string body = token[..separator];
        if (!TryFromBase64Url(token[(separator + 1)..], out byte[] provided)
            || !CryptographicOperations.FixedTimeEquals(Sign(body), provided))
            return false;

        if (!TryFromBase64Url(body, out byte[] payloadBytes))
            return false;

        string[] parts = Encoding.UTF8.GetString(payloadBytes).Split(Separator);
        if (parts.Length != 3 || !string.Equals(parts[0], expectedScope, StringComparison.Ordinal))
            return false;

        if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long expiresUnix))
            return false;

        expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresUnix);
        if (expiresAt <= DateTimeOffset.UtcNow)
            return false;

        subject = parts[1];
        return true;
    }

    byte[] Sign(string body)
    {
        using HMACSHA256 hmac = new(_key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
    }

    static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded
        };

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
