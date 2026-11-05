using Stackarr.Core.Configuration;

namespace Stackarr.Configuration.Tests;

public class RoutesConfigTests
{
    [Test]
    public async Task ResolvePublicBaseUrl_DefaultsToLocalhost()
    {
        RoutesConfig routes = new();

        await Assert.That(routes.ResolvePublicBaseUrl()).IsEqualTo("http://localhost");
    }

    [Test]
    public async Task ResolvePublicBaseUrl_UsesBaseUrl_AndTrimsTrailingSlash()
    {
        RoutesConfig routes = new() { BaseUrl = "https://media.example.com/" };

        await Assert.That(routes.ResolvePublicBaseUrl()).IsEqualTo("https://media.example.com");
    }

    [Test]
    public async Task ResolvePublicBaseUrl_PrefersExplicitPublicUrl()
    {
        RoutesConfig routes = new()
        {
            PublicUrl = "https://media.example.com/",
            BaseUrl = "http://localhost"
        };

        await Assert.That(routes.ResolvePublicBaseUrl()).IsEqualTo("https://media.example.com");
    }

    [Test]
    public async Task ResolvePublicBaseUrl_FallsBackToBaseUrl_WhenPublicUrlBlank()
    {
        RoutesConfig routes = new()
        {
            PublicUrl = "   ",
            BaseUrl = "https://base.example.com/"
        };

        await Assert.That(routes.ResolvePublicBaseUrl()).IsEqualTo("https://base.example.com");
    }

    [Test]
    public async Task ResolvePublicBaseUrl_FallsBackToLocalhost_WhenBothBlank()
    {
        RoutesConfig routes = new()
        {
            PublicUrl = "",
            BaseUrl = ""
        };

        await Assert.That(routes.ResolvePublicBaseUrl()).IsEqualTo("http://localhost");
    }
}
