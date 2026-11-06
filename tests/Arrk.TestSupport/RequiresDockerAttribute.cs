using TUnit.Core;

namespace Arrk.TestSupport;

/// <summary>
/// Skips Docker-backed tests when no daemon is reachable, or when
/// <c>ARRK_SKIP_DOCKER_TESTS=1</c> forces a hermetic run.
/// </summary>
/// <remarks>
/// The probe is lazy and shared by the whole test process, so a missing daemon costs one
/// failed connection instead of one per test. Apply it to a class, method or assembly.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Assembly)]
public sealed class RequiresDockerAttribute : SkipAttribute
{
    static readonly Lazy<Task<bool>> Available = new(DockerTestContainer.IsDockerAvailable);

    public RequiresDockerAttribute() : base("Docker is not available")
    {
    }

    public override async Task<bool> ShouldSkip(TestRegisteredContext context)
    {
        if (Environment.GetEnvironmentVariable("ARRK_SKIP_DOCKER_TESTS") == "1")
            return true;

        return !await Available.Value;
    }
}
