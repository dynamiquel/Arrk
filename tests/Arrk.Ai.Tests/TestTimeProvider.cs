using Arrk.Ai.Data;

namespace Arrk.Ai.Tests;

/// <summary>Advanceable clock for cache/budget expiry tests.</summary>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
