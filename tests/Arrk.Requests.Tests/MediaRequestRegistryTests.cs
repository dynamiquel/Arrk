using Arrk.Core.Enums;
using Arrk.Core.Requests;

namespace Arrk.Requests.Tests;

public class MediaRequestRegistryTests
{
    [Test]
    public async Task Duplicate_capability_throws_at_startup()
    {
        StubRequestHandler a = new("tv");
        StubRequestHandler b = new("alt");
        b.SupportedCapabilities.Clear();
        b.SupportedCapabilities.Add(new RequestCapability(new MediaDomain("movie"), RequestKind.Movie));

        await Assert.That(() => new MediaRequestRegistry([a, b]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Distinct_capabilities_coexist()
    {
        StubRequestHandler tv = new("tv");
        StubRequestHandler music = new("music");
        music.SupportedCapabilities.Clear();
        music.SupportedCapabilities.Add(new RequestCapability(new MediaDomain("music"), RequestKind.Artist));

        MediaRequestRegistry registry = new([tv, music]);

        await Assert.That(registry.GetHandler(new MediaDomain("movie"), RequestKind.Movie)).IsSameReferenceAs(tv);
        await Assert.That(registry.GetHandler(new MediaDomain("music"), RequestKind.Artist)).IsSameReferenceAs(music);
        await Assert.That(registry.GetHandler(new MediaDomain("tv"), RequestKind.Series)).IsSameReferenceAs(tv);
    }

    [Test]
    public async Task Availability_filters_capabilities()
    {
        StubRequestHandler handler = new("tv") { IsAvailable = false };
        MediaRequestRegistry registry = new([handler]);

        await Assert.That(registry.IsAvailable(new MediaDomain("movie"), RequestKind.Movie)).IsFalse();
        await Assert.That(registry.AvailableCapabilities.Count).IsEqualTo(0);

        handler.IsAvailable = true;

        await Assert.That(registry.IsAvailable(new MediaDomain("movie"), RequestKind.Movie)).IsTrue();
        await Assert.That(registry.AvailableDomains).Contains(new MediaDomain("movie"));
        await Assert.That(registry.KindsFor(new MediaDomain("tv"))).Contains(RequestKind.Series);
    }

    [Test]
    public async Task Unknown_capability_has_no_handler()
    {
        MediaRequestRegistry registry = new([new StubRequestHandler("tv")]);
        await Assert.That(registry.GetHandler(new MediaDomain("music"), RequestKind.Scene)).IsNull();
    }
}
