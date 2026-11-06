using Arrk.Core.Configuration;

namespace Arrk.Auth.Services;

/// <summary>Resolves the single canonical OIDC issuer URL for Arrk.</summary>
public static class OidcIssuer
{
    public static string Resolve(AuthConfig auth, RoutesConfig routes)
    {
        if (!string.IsNullOrWhiteSpace(auth.Provider.Issuer))
            return auth.Provider.Issuer.TrimEnd('/');

        return routes.ResolvePublicBaseUrl().TrimEnd('/');
    }
}
