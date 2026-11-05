using Stackarr.App.Services;
using Stackarr.Core.Configuration;

namespace Stackarr.App.Tests;

public class PublicSchemeResolverTests
{
    static RoutesConfig Routes(string publicUrl = "", string baseUrl = "http://localhost") => new()
    {
        PublicUrl = publicUrl,
        BaseUrl = baseUrl
    };

    [Test]
    public async Task ShouldForceHttps_WhenHostMatchesPublicUrl()
    {
        bool result = PublicSchemeResolver.ShouldForceHttps(
            isHttps: false,
            forwardedHost: null,
            requestHost: "media.example.com",
            Routes(publicUrl: "https://media.example.com"));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldForceHttps_PrefersForwardedHost()
    {
        bool result = PublicSchemeResolver.ShouldForceHttps(
            isHttps: false,
            forwardedHost: "media.example.com:443",
            requestHost: "internal:5186",
            Routes(publicUrl: "https://media.example.com:443"));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldForceHttps_WhenAlreadyHttps_IsFalse()
    {
        bool result = PublicSchemeResolver.ShouldForceHttps(
            isHttps: true,
            forwardedHost: null,
            requestHost: "media.example.com",
            Routes(publicUrl: "https://media.example.com"));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldForceHttps_WhenPublicUrlIsHttp_IsFalse()
    {
        bool result = PublicSchemeResolver.ShouldForceHttps(
            isHttps: false,
            forwardedHost: null,
            requestHost: "localhost",
            Routes(publicUrl: "http://localhost"));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldForceHttps_WhenHostMismatches_IsFalse()
    {
        bool result = PublicSchemeResolver.ShouldForceHttps(
            isHttps: false,
            forwardedHost: "other.example.com",
            requestHost: "other.example.com",
            Routes(publicUrl: "https://media.example.com"));

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task ShouldForceHttps_FallsBackToBaseUrl()
    {
        bool result = PublicSchemeResolver.ShouldForceHttps(
            isHttps: false,
            forwardedHost: null,
            requestHost: "base.example.com",
            Routes(publicUrl: "", baseUrl: "https://base.example.com"));

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ShouldForceHttps_WhenPublicUrlBlank_IsFalse()
    {
        bool result = PublicSchemeResolver.ShouldForceHttps(
            isHttps: false,
            forwardedHost: null,
            requestHost: "localhost",
            Routes(publicUrl: "", baseUrl: ""));

        await Assert.That(result).IsFalse();
    }
}
