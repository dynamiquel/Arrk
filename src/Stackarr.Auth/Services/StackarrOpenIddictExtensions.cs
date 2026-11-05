using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using Stackarr.Core;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Configuration;

namespace Stackarr.Auth.Services;

/// <summary>
/// Registers Stackarr's OpenID Connect provider. Shared by the app host and tests so the exact
/// production configuration is what gets exercised.
/// </summary>
public static class StackarrOpenIddictExtensions
{
    public static IServiceCollection AddStackarrOpenIddict(
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
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<StackarrDbContext>())
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
