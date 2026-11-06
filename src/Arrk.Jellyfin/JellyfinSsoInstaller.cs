using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Auth.Abstractions;
using Arrk.Core.Configuration;

namespace Arrk.Jellyfin;

/// <summary>
/// Installs and configures the Jellyfin OIDC plugin (OIDC RBAC) so Jellyfin users can sign in with
/// Arrk's OpenID Connect provider. Everything is driven through Jellyfin's own admin API:
/// add the plugin repository, install the package, restart, then write the plugin configuration.
/// Idempotent, safe to run on a schedule and on config change.
/// </summary>
public sealed class JellyfinSsoInstaller(
    JellyfinClient client,
    IOptionsMonitor<JellyfinConfig> config,
    IOptionsMonitor<AuthConfig> auth,
    IOptionsMonitor<RoutesConfig> routes,
    IOidcProviderInfo oidc,
    ILogger<JellyfinSsoInstaller> logger)
{
    /// <summary>GUID of the OIDC RBAC plugin (stable across versions).</summary>
    public const string PluginGuid = "d4e5f6a7-b8c9-0d1e-2f3a-4b5c6d7e8f90";
    public const string PluginName = "OIDC RBAC";

    const string RepositoryName = "Arrk Plugins";

    public async Task<bool> EnsureAsync(CancellationToken ct = default)
    {
        JellyfinConfig cfg = config.CurrentValue;
        AuthConfig authConfig = auth.CurrentValue;

        if (!cfg.OidcSso || !authConfig.Provider.Enabled)
            return false;

        string issuer = oidc.ResolveIssuer();
        string clientId = string.IsNullOrWhiteSpace(authConfig.Provider.JellyfinClientId)
            ? "jellyfin"
            : authConfig.Provider.JellyfinClientId;
        string providerId = string.IsNullOrWhiteSpace(authConfig.Provider.JellyfinProviderId)
            ? "stackarr"
            : authConfig.Provider.JellyfinProviderId;

        string? secret = await oidc.GetClientSecretAsync(ct);
        if (string.IsNullOrEmpty(secret))
        {
            logger.LogWarning("Jellyfin SSO: no OIDC client secret is available yet; skipping");
            return false;
        }

        JellyfinPublicSystemInfo info = await client.GetPublicSystemInfoAsync(ct);
        if (!info.StartupWizardCompleted)
        {
            logger.LogDebug("Jellyfin SSO: startup wizard not complete yet; skipping");
            return false;
        }

        bool restarted = false;

        // 1. Ensure the plugin is installed *and loaded* (its configuration endpoint resolves).
        if (!await IsPluginLoadedAsync(ct))
        {
            await InstallPluginAsync(cfg, ct);
            restarted = true;
        }

        // Jellyfin sits behind the reverse proxy; tell the plugin its public base URL so it builds
        // the callback redirect_uri from the public origin rather than the internal container IP
        // (which would not match the redirect URIs registered with Arrk).
        string jellyfinBaseUrl = $"{routes.CurrentValue.ResolvePublicBaseUrl().TrimEnd('/')}/jellyfin";

        // 2. Ensure the provider configuration is current. Jellyfin only applies written plugin
        //    configuration on restart, so restart once more when it changed.
        bool configChanged = await ConfigurePluginAsync(cfg, issuer, clientId, providerId, secret, jellyfinBaseUrl, ct);
        if (configChanged)
        {
            await RestartAndWaitAsync(ct);
            restarted = true;
        }

        logger.LogInformation("Jellyfin SSO configured (issuer={Issuer}, clientId={ClientId})", issuer, clientId);
        return restarted;
    }

    async Task RestartAndWaitAsync(CancellationToken ct)
    {
        try
        {
            await client.RestartAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Jellyfin restart request ended with an error (expected during restart)");
        }

        await WaitForRestartAsync(ct);
    }

    /// <summary>The plugin's configuration endpoint only resolves once the plugin is loaded.</summary>
    async Task<bool> IsPluginLoadedAsync(CancellationToken ct)
    {
        try
        {
            _ = await client.GetPluginConfigurationAsync(PluginGuid, ct);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    async Task InstallPluginAsync(JellyfinConfig cfg, CancellationToken ct)
    {
        logger.LogInformation("Installing Jellyfin OIDC plugin from {Url}", cfg.OidcPluginManifestUrl);

        List<JellyfinRepositoryInfo> repositories = await client.GetRepositoriesAsync(ct);
        if (!repositories.Any(r => string.Equals(r.Url, cfg.OidcPluginManifestUrl, StringComparison.OrdinalIgnoreCase)))
        {
            repositories.Add(new JellyfinRepositoryInfo { Name = RepositoryName, Url = cfg.OidcPluginManifestUrl, Enabled = true });
            await client.SetRepositoriesAsync(repositories, ct);
        }

        string? version = string.IsNullOrWhiteSpace(cfg.OidcPluginVersion) ? null : cfg.OidcPluginVersion;
        try
        {
            await client.InstallPackageAsync(PluginName, version, cfg.OidcPluginManifestUrl, PluginGuid, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already installed by a previous run; a restart below is still what loads it.
            logger.LogDebug(ex, "Jellyfin package was already installed");
        }

        // Plugins only load after a restart. The request drops the connection mid-flight, so a
        // failure here is expected; readiness is confirmed by the plugin loading below.
        await RestartAndWaitAsync(ct);
    }

    /// <summary>
    /// Waits for a restart to complete: the server must first be observed down, then back up with
    /// the OIDC plugin loaded. Requiring the down observation avoids accepting the pre-restart
    /// process (where the plugin is already loaded from a previous step).
    /// </summary>
    async Task WaitForRestartAsync(CancellationToken ct)
    {
        bool sawDown = false;

        for (int attempt = 0; attempt < 180; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            bool up = false;
            try
            {
                up = (await client.GetPublicSystemInfoAsync(ct)).StartupWizardCompleted;
            }
            catch
            {
                sawDown = true;
            }

            if (up && sawDown)
            {
                try
                {
                    await client.AuthenticateAsync(ct);
                    if (await IsPluginLoadedAsync(ct))
                        return;
                }
                catch
                {
                    // Still settling.
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new TimeoutException("Jellyfin did not restart with the OIDC plugin loaded.");
    }

    async Task<bool> ConfigurePluginAsync(
        JellyfinConfig cfg,
        string issuer,
        string clientId,
        string providerId,
        string secret,
        string jellyfinBaseUrl,
        CancellationToken ct)
    {
        Dictionary<string, object?> desired = BuildConfiguration(cfg, issuer, clientId, providerId, secret, jellyfinBaseUrl);

        try
        {
            JsonElement current = await client.GetPluginConfigurationAsync(PluginGuid, ct);
            if (ConfigurationsEquivalent(current, desired))
            {
                logger.LogDebug("Jellyfin SSO configuration already up to date");
                return false;
            }
        }
        catch (Exception ex)
        {
            // Missing/invalid current config: just write ours.
            logger.LogDebug(ex, "Could not read the current Jellyfin SSO configuration; writing it");
        }

        await client.UpdatePluginConfigurationAsync(PluginGuid, desired, ct);
        logger.LogInformation("Wrote Jellyfin SSO provider configuration");
        return true;
    }

    static Dictionary<string, object?> BuildConfiguration(
        JellyfinConfig cfg,
        string issuer,
        string clientId,
        string providerId,
        string secret,
        string jellyfinBaseUrl)
    {
        return new Dictionary<string, object?>
        {
            ["Providers"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["ProviderId"] = providerId,
                    ["DisplayName"] = "Arrk",
                    ["Authority"] = issuer,
                    ["ClientId"] = clientId,
                    ["ClientSecret"] = secret,
                    ["Scopes"] = "openid profile email roles",
                    ["RoleClaim"] = "roles",
                    ["UsernameClaim"] = "preferred_username",
                    ["DisplayNameClaim"] = "name",
                    ["PictureClaim"] = "picture",
                    ["SyncProfileImage"] = false,
                    ["Enabled"] = true,
                    ["ButtonColor"] = "#7c5cff",
                    ["ServerBaseUrl"] = jellyfinBaseUrl
                }
            },
            ["RoleMappings"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["RoleName"] = cfg.OidcAdminRole,
                    ["IsAdmin"] = true,
                    ["EnableAllLibraries"] = true,
                    ["EnableMediaPlayback"] = true,
                    ["EnableRemoteAccess"] = true,
                    ["EnableTranscoding"] = true,
                    ["Priority"] = 100
                },
                new Dictionary<string, object?>
                {
                    ["RoleName"] = ArrkRoles.User,
                    ["IsAdmin"] = false,
                    ["EnableAllLibraries"] = true,
                    ["EnableMediaPlayback"] = true,
                    ["EnableRemoteAccess"] = true,
                    ["EnableTranscoding"] = true,
                    ["Priority"] = 0
                }
            },
            ["DefaultProvider"] = providerId,
            ["AutoCreateUsers"] = true,
            ["DefaultRoleName"] = ArrkRoles.User
        };
    }

    static bool ConfigurationsEquivalent(JsonElement current, Dictionary<string, object?> desired)
    {
        if (current.ValueKind != JsonValueKind.Object)
            return false;

        // Only compare the fields Arrk owns; unrelated plugin settings are left alone.
        return Matches(current, "DefaultProvider", desired["DefaultProvider"])
            && Matches(current, "AutoCreateUsers", desired["AutoCreateUsers"])
            && ProviderMatches(current, desired)
            && RoleMappingsMatch(current, desired);
    }

    static bool ProviderMatches(JsonElement current, Dictionary<string, object?> desired)
    {
        if (!current.TryGetProperty("providers", out JsonElement providers) && !current.TryGetProperty("Providers", out providers))
            return false;
        if (providers.ValueKind != JsonValueKind.Array || providers.GetArrayLength() == 0)
            return false;

        JsonElement provider = providers[0];
        Dictionary<string, object?> wanted = (Dictionary<string, object?>)((List<object?>)desired["Providers"]!)[0]!;
        foreach (string key in new[] { "ProviderId", "Authority", "ClientId", "ClientSecret", "RoleClaim", "Scopes", "Enabled", "ServerBaseUrl" })
        {
            if (!Matches(provider, key, wanted[key]))
                return false;
        }

        return true;
    }

    static bool RoleMappingsMatch(JsonElement current, Dictionary<string, object?> desired)
    {
        if (!current.TryGetProperty("roleMappings", out JsonElement mappings) && !current.TryGetProperty("RoleMappings", out mappings))
            return false;
        if (mappings.ValueKind != JsonValueKind.Array)
            return false;

        List<object?> wanted = (List<object?>)desired["RoleMappings"]!;
        foreach (Dictionary<string, object?> mapping in wanted.Cast<Dictionary<string, object?>>())
        {
            string role = (string)mapping["RoleName"]!;
            JsonElement? match = mappings.EnumerateArray().FirstOrDefault(m => Matches(m, "RoleName", role));
            if (match is null || match.Value.ValueKind == JsonValueKind.Undefined)
                return false;
            if (!Matches(match.Value, "IsAdmin", mapping["IsAdmin"]))
                return false;
        }

        return true;
    }

    static bool Matches(JsonElement element, string property, object? expected)
    {
        if (!element.TryGetProperty(property, out JsonElement value)
            && !element.TryGetProperty(char.ToLowerInvariant(property[0]) + property[1..], out value))
            return false;

        return expected switch
        {
            null => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined,
            bool b => value.ValueKind == JsonValueKind.True == b || value.ValueKind == JsonValueKind.False == b,
            int i => value.ValueKind == JsonValueKind.Number && value.GetInt32() == i,
            string s => value.ValueKind == JsonValueKind.String && value.GetString() == s,
            _ => false
        };
    }
}
