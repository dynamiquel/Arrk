using System.Security.Cryptography;
using System.Text.Json;

namespace Stackarr.Navidrome;

/// <summary>
/// The credentials Stackarr uses for Navidrome's Subsonic API. Prefers the values in
/// <c>services:navidrome</c>; when no password is configured, a strong one is generated once and
/// persisted beside the config so it survives restarts (and is reused for the auto-provisioned
/// Navidrome user).
/// </summary>
public sealed class NavidromeApiSecret
{
    public string Username { get; init; } = "stackarr";
    public string Password { get; init; } = "";
    public bool WasConfigured { get; init; }

    public static NavidromeApiSecret Load(string? configDir, string? configuredUsername, string? configuredPassword)
    {
        string username = string.IsNullOrWhiteSpace(configuredUsername) ? "stackarr" : configuredUsername;
        if (!string.IsNullOrWhiteSpace(configuredPassword))
            return new NavidromeApiSecret { Username = username, Password = configuredPassword, WasConfigured = true };

        string? file = string.IsNullOrWhiteSpace(configDir) ? null : Path.Combine(configDir, "navidrome-api.json");
        if (file is not null && File.Exists(file))
        {
            try
            {
                StoredCredentials? stored = JsonSerializer.Deserialize<StoredCredentials>(File.ReadAllText(file));
                if (stored is not null && !string.IsNullOrWhiteSpace(stored.Password))
                    return new NavidromeApiSecret { Username = stored.Username, Password = stored.Password };
            }
            catch { /* fall through to regenerate */ }
        }

        NavidromeApiSecret generated = new()
        {
            Username = username,
            Password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24))
        };
        if (file is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, JsonSerializer.Serialize(
                    new StoredCredentials { Username = generated.Username, Password = generated.Password },
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* best effort; the generated password still works for this session */ }
        }
        return generated;
    }

    sealed class StoredCredentials
    {
        public string Username { get; set; } = "stackarr";
        public string Password { get; set; } = "";
    }
}
