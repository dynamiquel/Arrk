using Microsoft.Extensions.Options;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Auth.Services;

/// <summary>
/// The merged list of built-in and module-contributed sign-in providers. Discord is built into the
/// shell; everything else is discovered from DI.
/// </summary>
public sealed class LoginProviderRegistry(
    IOptionsMonitor<AuthConfig> auth,
    IEnumerable<IExternalLoginProvider> external)
{
    public const string DiscordScheme = ExternalLoginSchemes.Discord;

    public IReadOnlyList<LoginProvider> All
    {
        get
        {
            List<LoginProvider> providers = [];
            AuthConfig current = auth.CurrentValue;
            if (current.External.Discord.Enabled && !string.IsNullOrWhiteSpace(current.External.Discord.ClientId))
                providers.Add(new LoginProvider(DiscordScheme, current.External.Discord.DisplayName));

            foreach (IExternalLoginProvider provider in external)
            {
                if (provider.IsEnabled)
                    providers.Add(new LoginProvider(provider.Scheme, provider.DisplayName));
            }

            return providers;
        }
    }

    /// <summary>Providers offered on the sign-in page (a provider may be link-only).</summary>
    public IReadOnlyList<LoginProvider> Login =>
        All.Where(p => p.Scheme == DiscordScheme
            || external.FirstOrDefault(x => x.Scheme == p.Scheme)?.ShowOnLogin != false).ToList();

    public bool IsEnabled(string scheme) =>
        All.Any(p => string.Equals(p.Scheme, scheme, StringComparison.Ordinal));

    public IExternalLoginProvider? Find(string scheme) =>
        external.FirstOrDefault(p => p.IsEnabled && string.Equals(p.Scheme, scheme, StringComparison.Ordinal));
}
