using Stackarr.Prowlarr.Configuration;

namespace Stackarr.Prowlarr.Tests;

public class SolverConfigTests
{
    [Test]
    public async Task ResolveHost_PrefersExplicitOverride()
    {
        SolverConfig solver = new() { Host = "http://byparr.example:8191/" };

        await Assert.That(solver.ResolveHost("http://stackarr-test-byparr:8191/"))
            .IsEqualTo("http://byparr.example:8191/");
    }

    [Test]
    public async Task ResolveHost_FallsBackToContainerUrl_AndIgnoresWhitespace()
    {
        await Assert.That(new SolverConfig().ResolveHost("http://stackarr-test-byparr:8191/"))
            .IsEqualTo("http://stackarr-test-byparr:8191/");
        await Assert.That(new SolverConfig { Host = "   " }.ResolveHost("http://stackarr-test-byparr:8191/"))
            .IsEqualTo("http://stackarr-test-byparr:8191/");
    }

    [Test]
    public async Task ResolveTag_FallsBackToDefault_WhenBlank_AndTrims()
    {
        await Assert.That(new SolverConfig { Tag = "  " }.ResolveTag()).IsEqualTo(SolverConfig.DefaultTag);
        await Assert.That(new SolverConfig { Tag = "  cloudflare  " }.ResolveTag()).IsEqualTo("cloudflare");
    }

    [Test]
    public async Task ResolveImage_AndTimeout_FallBackToDefaults()
    {
        await Assert.That(new SolverConfig { Image = " " }.ResolveImage()).IsEqualTo(SolverConfig.DefaultImage);
        await Assert.That(new SolverConfig { RequestTimeoutSeconds = 0 }.ResolveRequestTimeoutSeconds())
            .IsEqualTo(SolverConfig.DefaultRequestTimeoutSeconds);
        await Assert.That(new SolverConfig { RequestTimeoutSeconds = 240 }.ResolveRequestTimeoutSeconds())
            .IsEqualTo(240);
    }
}
