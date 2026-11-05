using Stackarr.TestSupport;

namespace Stackarr.Docker.Tests;

/// <summary>
/// Session-level safety net: removes any Docker resource this run created but a test failed to
/// clean up. Scoped to this run's identity, so a concurrently executing test process is never
/// affected.
/// </summary>
public static class DockerSessionCleanup
{
    [After(TestSession)]
    public static Task CleanupAsync() => DockerTestCleanup.RemoveRunAsync();
}
