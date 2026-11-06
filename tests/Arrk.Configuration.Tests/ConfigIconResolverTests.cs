using Arrk.Configuration;

namespace Arrk.Configuration.Tests;

/// <summary>
/// The visual config editor resolves icon names supplied by config section owners.
/// This keeps the UI module-agnostic: modules only hand over a name.
/// </summary>
public class ConfigIconResolverTests
{
    [Test]
    public async Task Resolve_KnownIconName_ReturnsPath()
    {
        string fallback = ConfigIconResolver.Resolve("Tune");

        await Assert.That(fallback).IsNotEmpty();
        await Assert.That(ConfigIconResolver.Resolve("Tv")).IsNotEmpty();
        await Assert.That(ConfigIconResolver.Resolve("Download")).IsNotEmpty();
    }

    [Test]
    public async Task Resolve_IsCaseInsensitive()
    {
        await Assert.That(ConfigIconResolver.Resolve("tune"))
            .IsEqualTo(ConfigIconResolver.Resolve("Tune"));
    }

    [Test]
    public async Task Resolve_UnknownOrMissingName_FallsBack()
    {
        string fallback = ConfigIconResolver.Resolve("Tune");

        await Assert.That(ConfigIconResolver.Resolve(null)).IsEqualTo(fallback);
        await Assert.That(ConfigIconResolver.Resolve("")).IsEqualTo(fallback);
        await Assert.That(ConfigIconResolver.Resolve("NotARealIconName")).IsEqualTo(fallback);
    }
}
