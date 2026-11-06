using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Arrk.Core;
using Arrk.Auth.Abstractions;
using Arrk.Core.Configuration;

namespace Arrk.Auth.Services;

/// <summary>
/// Registers Arrk's OpenID Connect provider. Shared by the app host and tests so the exact
/// production configuration is what gets exercised.
/// </summary>
public static class ArrkOpenIddictExtensions
{
    public static IServiceCollection AddArrkOpenIddict(
        this IServiceCollection services,
        AuthConfig auth,
        RoutesConfig routes,
        string configDirectory)
    {
        if (!auth.Provider.Enabled)
            return services;

        string issuer = OidcIssuer.Resolve(auth, routes);
        OpenIddictCertificates certificates = OpenIddictKeyStore.Ensure(configDirectory);

        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<ArrkDbContext>())
            .AddServer(server =>
            {
                server
                    .SetAuthorizationEndpointUris("/connect/authorize")
                    .SetTokenEndpointUris("/connect/token")
                    .SetUserInfoEndpointUris("/connect/userinfo");

                server.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
                server.RequireProofKeyForCodeExchange();
                server.RegisterScopes(
                    OpenIddictConstants.Scopes.OpenId,
                    OpenIddictConstants.Scopes.Email,
                    OpenIddictConstants.Scopes.Profile,
                    "roles");

                server.AddSigningCertificate(certificates.Signing);
                server.AddEncryptionCertificate(certificates.Encryption);

                if (!string.IsNullOrWhiteSpace(issuer))
                    server.SetIssuer(new Uri(issuer));

                server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableUserInfoEndpointPassthrough();

                if (!auth.RequireHttps)
                    server.UseAspNetCore().DisableTransportSecurityRequirement();
            });

        services.AddHostedService<OidcClientSeeder>();

        return services;
    }
}
