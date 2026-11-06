using Microsoft.Extensions.Options;
using Arrk.Auth.Abstractions;
using Arrk.Core.Configuration;
using Arrk.Core.Services;

namespace Arrk.Auth.Services;

public sealed class OidcProviderInfo(
    IOptionsMonitor<AuthConfig> auth,
    IOptionsMonitor<RoutesConfig> routes,
    SettingsService settings) : IOidcProviderInfo
{
    public string ResolveIssuer() => OidcIssuer.Resolve(auth.CurrentValue, routes.CurrentValue);

    public Task<string?> GetClientSecretAsync(CancellationToken ct = default) =>
        settings.GetAsync(OidcClientSeeder.JellyfinSecretKey, ct);
}
