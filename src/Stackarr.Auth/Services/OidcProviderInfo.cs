using Microsoft.Extensions.Options;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.Core.Services;

namespace Stackarr.Auth.Services;

public sealed class OidcProviderInfo(
    IOptionsMonitor<AuthConfig> auth,
    IOptionsMonitor<RoutesConfig> routes,
    SettingsService settings) : IOidcProviderInfo
{
    public string ResolveIssuer() => OidcIssuer.Resolve(auth.CurrentValue, routes.CurrentValue);

    public Task<string?> GetClientSecretAsync(CancellationToken ct = default) =>
        settings.GetAsync(OidcClientSeeder.JellyfinSecretKey, ct);
}
