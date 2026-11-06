namespace Arrk.TestSupport;

/// <summary>
/// A unique identity for the currently executing test process. Every Docker resource a test
/// creates is tagged with (or named after) this run id so that tests running concurrently in
/// other processes can never collide with, or be cleaned up by, this one. Cleanup is always
/// scoped to this run only.
/// </summary>
public static class TestRun
{
    /// <summary>
    /// Unique, Docker-name-safe token for this test process. Can be pinned by setting the
    /// <c>ARRK_TEST_RUN_ID</c> environment variable (useful for diagnosing a specific run).
    /// </summary>
    public static string Id { get; } = ResolveId();

    /// <summary>Prefix shared by every <c>ArrkInstance</c> StackName created in this run.</summary>
    public static string InstancePrefix => $"test-{Id}";

    /// <summary>
    /// Docker network every test <c>ArrkInstance</c> joins. Tests share one network instead of
    /// creating one per instance/run so the suite does not exhaust Docker's small default address
    /// pool. Container names remain run-scoped, so concurrent runs never collide.
    /// </summary>
    public const string SharedNetworkName = "stackarr-tests-net";

    /// <summary>Label stamped on every container/network a test creates.</summary>
    public const string RunLabel = "arrk.test-run-id";

    static string ResolveId()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("ARRK_TEST_RUN_ID");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Sanitize(fromEnv);

        // PID keeps concurrent processes distinct; the random suffix keeps successive runs of the
        // same process id distinct.
        return Sanitize($"{Environment.ProcessId:x}{Guid.NewGuid():N}");
    }

    static string Sanitize(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
                buffer[length++] = c;
        }

        return length >= 6 ? new string(buffer[..Math.Min(length, 16)]) : Guid.NewGuid().ToString("N")[..12];
    }
}
