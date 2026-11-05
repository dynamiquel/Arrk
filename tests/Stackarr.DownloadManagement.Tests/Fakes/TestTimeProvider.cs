namespace Stackarr.DownloadManagement.Tests;

/// <summary>
/// Deterministic clock so time-based cleanup logic can be tested without waiting.
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;

    public void AdvanceMinutes(double minutes) => Now = Now.AddMinutes(minutes);
}
