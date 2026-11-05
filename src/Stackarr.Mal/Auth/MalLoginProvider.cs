using Microsoft.Extensions.Options;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Mal.Auth;

/// <summary>Adds MyAnimeList to the shell's sign-in provider list when OAuth is configured.</summary>
public sealed class MalLoginProvider(IOptionsMonitor<MalConfig> config) : IExternalLoginProvider
{
    public const string Scheme = "mal";

    string IExternalLoginProvider.Scheme => Scheme;

    public string DisplayName => "MyAnimeList";

    public bool IsEnabled => config.CurrentValue.Enabled && config.CurrentValue.IsConfigured;

    public bool ShowOnLogin => config.CurrentValue.AllowLogin;

    public string BuildStartUrl(string mode, int? linkUserId, string? returnUrl)
    {
        string url = $"/mal-signin/start?mode={Uri.EscapeDataString(mode)}" +
                     $"&returnUrl={Uri.EscapeDataString(returnUrl ?? "/mal")}";
        if (linkUserId is int id)
            url += $"&userId={id}";
        return url;
    }
}
