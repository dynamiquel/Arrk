using Arrk.Core.Services;

namespace Arrk.Core.Tests;

/// <summary>
/// Covers the live activity registry: an in-flight operation registers with its category and detail,
/// can update its detail, and removes itself when its scope is disposed (so finished operations
/// disappear rather than lingering).
/// </summary>
public class LiveActivityRegistryTests
{
    [Test]
    public async Task Begin_PublishesAnEntryWithItsKindAndDetail()
    {
        LiveActivityRegistry registry = new();

        using LiveActivityScope scope = registry.Begin(LiveActivityKind.Search, "Searching \"Song\"", "Looking for music · Soulseek");

        await Assert.That(registry.Count).IsEqualTo(1);
        LiveActivityEntry entry = registry.Active.Single();
        await Assert.That(entry.Kind).IsEqualTo(LiveActivityKind.Search);
        await Assert.That(entry.Title).IsEqualTo("Searching \"Song\"");
        await Assert.That(entry.Detail).IsEqualTo("Looking for music · Soulseek");
        await Assert.That(entry.Id).IsEqualTo(scope.Id);
    }

    [Test]
    public async Task Update_ReplacesTheDetail()
    {
        LiveActivityRegistry registry = new();
        using LiveActivityScope scope = registry.Begin(LiveActivityKind.Search, "Searching \"Song\"", "Looking for music");

        scope.Update("Looking for music · torrents via Prowlarr");

        await Assert.That(registry.Active.Single().Detail).IsEqualTo("Looking for music · torrents via Prowlarr");
    }

    [Test]
    public async Task Dispose_RemovesTheEntry()
    {
        LiveActivityRegistry registry = new();
        LiveActivityScope scope = registry.Begin(LiveActivityKind.Import, "Importing \"Album\"");

        await Assert.That(registry.Count).IsEqualTo(1);

        scope.Dispose();
        scope.Dispose(); // idempotent

        await Assert.That(registry.Count).IsEqualTo(0);
        await Assert.That(registry.Active).IsEmpty();
    }

    [Test]
    public async Task Changed_FiresOnBeginUpdateAndDispose()
    {
        LiveActivityRegistry registry = new();
        int changes = 0;
        registry.Changed += () => changes++;

        LiveActivityScope scope = registry.Begin(LiveActivityKind.Container, "Starting api");
        scope.Update("Pulling image");
        scope.Dispose();

        await Assert.That(changes).IsEqualTo(3);
    }
}
