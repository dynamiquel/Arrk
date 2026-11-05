using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// Installs and configures the Jellyfin OIDC plugin against a real Jellyfin 12.2 container, then
/// verifies the plugin is loaded, the provider/role configuration was written, and the SSO start
/// endpoint redirects to the configured authority.
/// </summary>
[NotInParallel(JellyfinServerShared.ParallelKey)]
public class JellyfinSsoInstallerIntegrationTests
{
    static Task<JellyfinServer?> ServerAsync() => JellyfinServerShared.GetServerAsync();

    [Test]
    public async Task EnsureAsync_InstallsAndConfiguresOidcPlugin()
    {
        JellyfinServer? server = await ServerAsync();
        if (server is null) return;

        // Use a real, reachable OIDC authority so the plugin's discovery + start redirect work.
        const string issuer = "https://discord.com";

        JellyfinConfig jellyfinConfig = new()
        {
            OidcSso = true,
            OidcPluginManifestUrl = "https://raw.githubusercontent.com/Ezeqielle/jellyfin-plugin-oidc/main/manifest.json",
            OidcAdminRole = "Admin"
        };

        AuthConfig auth = new();
        auth.Provider.Enabled = true;
        auth.Provider.JellyfinClientId = "jellyfin";
        auth.Provider.JellyfinProviderId = "stackarr";

        RoutesConfig routes = new() { PublicUrl = issuer };

        JellyfinSsoInstaller installer = new(
            server.CreateClient(),
            new MutableOptionsMonitor<JellyfinConfig>(jellyfinConfig),
            new MutableOptionsMonitor<AuthConfig>(auth),
            new MutableOptionsMonitor<RoutesConfig>(routes),
            new FakeOidcProviderInfo(issuer, "test-client-secret"),
            NullLogger<JellyfinSsoInstaller>.Instance);

        await installer.EnsureAsync();

        // 1. The plugin is installed and loaded. Jellyfin returns plugin ids without hyphens.
        JellyfinClient client = server.CreateClient();
        List<JellyfinPluginInfo> plugins = await client.GetPluginsAsync();
        string pluginDump = string.Join(" | ", plugins.Select(p => $"{p.Name}={p.Id} (v{p.Version}, {p.Status})"));
        await Assert.That(plugins.Any(p => Normalize(p.Id) == Normalize(JellyfinSsoInstaller.PluginGuid))).IsTrue()
            .Because(pluginDump);

        // 2. The provider configuration was written.
        JsonElement config = await client.GetPluginConfigurationAsync(JellyfinSsoInstaller.PluginGuid);
        JsonElement provider = FirstProvider(config);
        await Assert.That(GetString(provider, "ProviderId")).IsEqualTo("stackarr");
        await Assert.That(GetString(provider, "Authority")).IsEqualTo(issuer);
        await Assert.That(GetString(provider, "ClientId")).IsEqualTo("jellyfin");
        await Assert.That(GetString(provider, "RoleClaim")).IsEqualTo("roles");
        await Assert.That(GetString(config, "DefaultProvider")).IsEqualTo("stackarr");

        JsonElement adminMapping = FirstRoleMapping(config, "Admin");
        await Assert.That(GetBool(adminMapping, "IsAdmin")).IsTrue();

        // 3. The SSO start endpoint redirects to the authority with the client id.
        using HttpClientHandler handler = new() { AllowAutoRedirect = false };
        using HttpClient http = new(handler) { BaseAddress = new Uri(server.Url) };
        HttpResponseMessage start = await http.GetAsync("/sso/OIDC/Start/stackarr");
        await Assert.That(start.StatusCode).IsEqualTo(HttpStatusCode.Found);
        string location = start.Headers.Location!.ToString();
        await Assert.That(location).Contains("discord.com");
        await Assert.That(location).Contains("client_id=jellyfin");

        // The callback must be built from the configured public Jellyfin base URL, not the
        // internal container address.
        await Assert.That(QueryParam(location, "redirect_uri"))
            .IsEqualTo("https://discord.com/jellyfin/sso/OIDC/Callback/stackarr");

        // 4. The login-button script (loaded via the branding Login Disclaimer) offers the SSO
        //    sign-in link.
        HttpResponseMessage buttons = await http.GetAsync("/sso/OIDC/LoginButtons");
        await Assert.That(buttons.StatusCode).IsEqualTo(HttpStatusCode.OK);
        string script = await buttons.Content.ReadAsStringAsync();
        await Assert.That(script).Contains("sso/OIDC/Start/stackarr");
    }

    static string Normalize(string id) => id.Replace("-", "").ToLowerInvariant();

    static string? QueryParam(string url, string key)
    {
        int q = url.IndexOf('?');
        if (q < 0)
            return null;

        foreach (string pair in url[(q + 1)..].Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return null;
    }

    static JsonElement FirstProvider(JsonElement config)
    {
        JsonElement providers = GetArray(config, "providers", "Providers");
        return providers.ValueKind == JsonValueKind.Array && providers.GetArrayLength() > 0 ? providers[0] : default;
    }

    static JsonElement FirstRoleMapping(JsonElement config, string role)
    {
        JsonElement mappings = GetArray(config, "roleMappings", "RoleMappings");
        foreach (JsonElement mapping in mappings.EnumerateArray())
        {
            if (GetString(mapping, "RoleName") == role)
                return mapping;
        }

        return default;
    }

    static JsonElement GetArray(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array)
                return value;
        }

        return default;
    }

    static string? GetString(JsonElement element, string name)
    {
        foreach (string candidate in new[] { name, char.ToLowerInvariant(name[0]) + name[1..] })
        {
            if (element.TryGetProperty(candidate, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    static bool GetBool(JsonElement element, string name)
    {
        foreach (string candidate in new[] { name, char.ToLowerInvariant(name[0]) + name[1..] })
        {
            if (element.TryGetProperty(candidate, out JsonElement value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        }

        return false;
    }

    sealed class FakeOidcProviderInfo(string issuer, string secret) : IOidcProviderInfo
    {
        public string ResolveIssuer() => issuer;

        public Task<string?> GetClientSecretAsync(CancellationToken ct = default) => Task.FromResult<string?>(secret);
    }
}
