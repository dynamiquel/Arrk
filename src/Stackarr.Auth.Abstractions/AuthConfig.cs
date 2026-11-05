using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Stackarr.Auth.Abstractions;

/// <summary>
/// Authentication configuration. Stackarr is the account authority and an OpenID Connect
/// provider; upstream providers (Discord, later Google/GitHub) federate into it. Passwords are
/// independent of the login method: they exist to satisfy media players that have no OIDC.
/// </summary>
public class AuthConfig
{
    public const string SectionName = "auth";

    [Description("Username for the automatically created first administrator account")]
    [Required(AllowEmptyStrings = false)]
    [MinLength(3)]
    public string AdminUsername { get; set; } = "admin";

    [Description("Password for the automatically created first administrator account")]
    [DataType(DataType.Password)]
    public string AdminPassword { get; set; } = "admin";

    [Description("Lifetime of a Stackarr login session in days")]
    [Range(1, 365)]
    public int SessionDays { get; set; } = 14;

    [Description("Require HTTPS for the authentication cookie (enable when Stackarr is served over TLS)")]
    public bool RequireHttps { get; set; }

    [Description("Maximum sign-in, sign-up and OAuth-start requests per minute per client IP. 0 disables the limit.")]
    [Range(0, 10000)]
    public int AuthRateLimitPerMinute { get; set; } = 10;

    [Description("Allow signing in to Stackarr with a username and password. Does not control self-service sign-up.")]
    public bool AllowPasswordLogin { get; set; } = true;

    [Description("Allow users to create a local username/password account at /register. Independent of whether password sign-in is allowed.")]
    public bool AllowLocalSignUp { get; set; } = true;

    [Description("How accounts are created: Open (anyone can sign up), Approval (sign-ups wait for an admin), AdminOnly (admins create accounts)")]
    public RegistrationMode RegistrationMode { get; set; } = RegistrationMode.Approval;

    [Description("Stackarr acting as the OpenID Connect provider for media services")]
    public StackarrProviderConfig Provider { get; set; } = new();

    [Description("Upstream identity providers that federate into Stackarr")]
    public ExternalProvidersConfig External { get; set; } = new();

    [Description("Media-player account/password provisioning (independent of Stackarr login)")]
    public PlayerProvisioningConfig Players { get; set; } = new();
}

/// <summary>Stackarr's own OpenID Connect provider settings.</summary>
public class StackarrProviderConfig
{
    [Description("Enable Stackarr as an OIDC provider for Jellyfin/Navidrome and other clients")]
    public bool Enabled { get; set; } = true;

    [Description("Override the OIDC issuer. Defaults to routes.public_url, then routes.base_url.")]
    public string Issuer { get; set; } = "";

    [Description("OpenID client id registered for Jellyfin's OIDC plugin")]
    public string JellyfinClientId { get; set; } = "jellyfin";

    [Description("Provider id used by Jellyfin's OIDC plugin (callback path segment)")]
    public string JellyfinProviderId { get; set; } = "stackarr";

    [Description("Redirect URIs registered for the Jellyfin OIDC client (defaults to the plugin callback)")]
    public List<string> JellyfinRedirectUris { get; set; } = [];
}

/// <summary>Upstream OIDC providers users can sign in with.</summary>
public class ExternalProvidersConfig
{
    public DiscordConfig Discord { get; set; } = new();
}

public class DiscordConfig
{
    [Description("Enable sign-in with Discord")]
    public bool Enabled { get; set; }

    [Description("Discord OAuth2/OIDC client id")]
    public string ClientId { get; set; } = "";

    [Description("Discord OAuth2/OIDC client secret")]
    [DataType(DataType.Password)]
    public string ClientSecret { get; set; } = "";

    [Description("Label shown on the sign-in button")]
    public string DisplayName { get; set; } = "Discord";

    [Description("Authority/issuer URL. Defaults to https://discord.com; override for testing.")]
    public string Authority { get; set; } = "https://discord.com";

    [Description("Callback path (must match the redirect URI registered with the provider)")]
    public string CallbackPath { get; set; } = "/signin-discord";
}

/// <summary>
/// Player account/password provisioning. This is separate from
/// <see cref="AuthConfig.AllowPasswordLogin"/>: some media players (Infuse, Subsonic apps) have
/// no OIDC and must be given a username/password regardless of how the user signs into Stackarr.
/// </summary>
public class PlayerProvisioningConfig
{
    [Description("Mirror Stackarr accounts into media players that support it (Jellyfin)")]
    public bool ProvisionAccounts { get; set; } = true;

    [Description("Forward passwords to media players so clients without OIDC (e.g. Infuse) work")]
    public bool ProvisionPasswords { get; set; } = true;
}
