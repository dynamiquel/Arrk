using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using Arrk.Core.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Arrk.Auth.Services;

/// <summary>
/// Ensures the OpenID Connect client used by Jellyfin's OIDC plugin exists, with a secret that is
/// generated once, stored durably, and reused for the plugin's configuration. Also exposes the
/// issuer/secret so the Jellyfin module can configure the plugin programmatically.
/// </summary>
public sealed class OidcClientSeeder(
    IServiceScopeFactory scopeFactory,
    SettingsService settings,
    IOptionsMonitor<AuthConfig> auth,
    IOptionsMonitor<RoutesConfig> routes,
    ILogger<OidcClientSeeder> logger) : IHostedService
{
    public const string JellyfinClientId = "jellyfin";
    public const string JellyfinSecretKey = "oidc:jellyfin:client_secret";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        AuthConfig config = auth.CurrentValue;
        if (!config.Provider.Enabled)
            return;

        try
        {
            // OpenIddict's managers are scoped, so they cannot be injected into this singleton
            // hosted service; resolve one from a short-lived scope instead.
            await using AsyncServiceScope serviceScope = scopeFactory.CreateAsyncScope();
            IOpenIddictApplicationManager applications =
                serviceScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

            string clientId = string.IsNullOrWhiteSpace(config.Provider.JellyfinClientId)
                ? JellyfinClientId
                : config.Provider.JellyfinClientId;

            string secret = await settings.GetOrCreateAsync(
                JellyfinSecretKey,
                () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                cancellationToken);

            string[] redirectUris = ResolveRedirectUris(config);

            OpenIddictApplicationDescriptor descriptor = new()
            {
                ClientId = clientId,
                ClientSecret = secret,
                ClientType = ClientTypes.Confidential,
                DisplayName = "Jellyfin",
                ConsentType = ConsentTypes.Implicit
            };

            foreach (string uri in redirectUris)
                descriptor.RedirectUris.Add(new Uri(uri));

            foreach (string scope in new[] { Scopes.OpenId, Scopes.Profile, Scopes.Email, "roles" })
                descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);

            descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
            descriptor.Permissions.Add(Permissions.Endpoints.Token);
            descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
            descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
            descriptor.Permissions.Add(Requirements.Features.ProofKeyForCodeExchange);

            object? existing = await applications.FindByClientIdAsync(clientId, cancellationToken);
            if (existing is null)
            {
                await applications.CreateAsync(descriptor, cancellationToken);
                logger.LogInformation("Registered OIDC client '{ClientId}' for Jellyfin", clientId);
            }
            else
            {
                await applications.UpdateAsync(existing, descriptor, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register the Jellyfin OIDC client.");
        }
    }

    string[] ResolveRedirectUris(AuthConfig config)
    {
        if (config.Provider.JellyfinRedirectUris.Count > 0)
            return [.. config.Provider.JellyfinRedirectUris];

        string baseUrl = routes.CurrentValue.ResolvePublicBaseUrl().TrimEnd('/');
        // Match the historical default registered in existing databases and player configs.
        string providerId = string.IsNullOrWhiteSpace(config.Provider.JellyfinProviderId)
            ? "stackarr"
            : config.Provider.JellyfinProviderId;

        // The OIDC RBAC plugin's callback path includes the provider id.
        return
        [
            $"{baseUrl}/jellyfin/sso/OIDC/Callback/{providerId}",
            $"{baseUrl}/jellyfin/sso/OIDC/Auth/{providerId}"
        ];
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
