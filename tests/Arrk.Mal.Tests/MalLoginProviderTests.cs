using Microsoft.Extensions.Options;
using Arrk.Mal.Auth;
using Arrk.TestSupport;

namespace Arrk.Mal.Tests;

public class MalLoginProviderTests
{
    [Test]
    public async Task Enabled_WhenOAuthClientAndLoginAreConfigured()
    {
        MalConfig config = new()
        {
            ClientId = "client",
            ClientSecret = "secret",
            AllowLogin = true
        };
        MalLoginProvider provider = new(new MutableOptionsMonitor<MalConfig>(config));

        await Assert.That(provider.IsEnabled).IsTrue();
        await Assert.That(provider.DisplayName).IsEqualTo("MyAnimeList");
    }

    [Test]
    public async Task Disabled_WithoutASecret()
    {
        MalLoginProvider provider = new(new MutableOptionsMonitor<MalConfig>(new MalConfig
        {
            ClientId = "client",
            ClientSecret = ""
        }));

        await Assert.That(provider.IsEnabled).IsFalse();
    }

    [Test]
    public async Task StartUrl_CarriesLinkModeAndUser()
    {
        MalLoginProvider provider = new(new MutableOptionsMonitor<MalConfig>(new MalConfig
        {
            ClientId = "client",
            ClientSecret = "secret"
        }));

        string url = provider.BuildStartUrl("link", 42, "/account");

        await Assert.That(url).Contains("mode=link");
        await Assert.That(url).Contains("userId=42");
    }
}
