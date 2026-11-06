using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Arrk.App.Services;

namespace Arrk.App.Tests;

/// <summary>
/// Pins the headers and trusted-proxy defaults that are easy to regress: a missing frame policy
/// enables clickjacking, and trusting every forwarded header lets a direct client spoof its address.
/// </summary>
public class SecurityHardeningTests
{
    [Test]
    public async Task SecurityHeaders_FrameMimeAndReferrerAreLockedDown()
    {
        HeaderDictionary headers = [];

        SecurityHeaders.Apply(headers);

        await Assert.That(headers["X-Content-Type-Options"].ToString()).IsEqualTo("nosniff");
        await Assert.That(headers["X-Frame-Options"].ToString()).IsEqualTo("DENY");
        await Assert.That(headers["Content-Security-Policy"].ToString()).Contains("frame-ancestors 'none'");
        await Assert.That(headers["Referrer-Policy"].ToString()).IsEqualTo("no-referrer");
        await Assert.That(headers["Permissions-Policy"].ToString()).Contains("geolocation=()");
    }

    [Test]
    public async Task ForwardedHeaders_TrustDockerAndPrivateRangesButNotThePublicInternet()
    {
        ForwardedHeadersOptions options = new();

        ForwardedHeadersConfiguration.Configure(options);

        await Assert.That(options.KnownIPNetworks.Any(n => n.Contains(IPAddress.Parse("172.18.0.5")))).IsTrue();
        await Assert.That(options.KnownIPNetworks.Any(n => n.Contains(IPAddress.Parse("10.1.2.3")))).IsTrue();
        await Assert.That(options.KnownIPNetworks.Any(n => n.Contains(IPAddress.Parse("192.168.1.50")))).IsTrue();
        await Assert.That(options.KnownIPNetworks.Any(n => n.Contains(IPAddress.Parse("203.0.113.10")))).IsFalse();
        await Assert.That(options.KnownProxies.Contains(IPAddress.Loopback)).IsTrue();
    }
}
