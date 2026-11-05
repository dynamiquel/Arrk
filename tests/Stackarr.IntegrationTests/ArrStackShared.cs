namespace Stackarr.IntegrationTests;

/// <summary>
/// Shares a single <see cref="ArrStackFixture"/> across every integration test class that needs the
/// Sonarr + Radarr + Prowlarr + Transmission stack. Sharing one fixture avoids name/port races and
/// redundant container startup. All consumers must be marked <c>[NotInParallel("docker-arr-stack")]</c>.
/// </summary>
public static class ArrStackShared
{
    public const string ParallelKey = "docker";

    static readonly SemaphoreSlim Lock = new(1, 1);
    static ArrStackFixture? _fixture;

    public static async Task<ArrStackFixture?> GetAsync()
    {
        await Lock.WaitAsync();
        try
        {
            if (_fixture is not null)
                return _fixture;

            ArrStackFixture fixture = new();
            if (!await fixture.StartAsync())
            {
                await fixture.DisposeAsync();
                return null;
            }

            _fixture = fixture;
            return _fixture;
        }
        finally
        {
            Lock.Release();
        }
    }

    public static async Task DisposeAsync()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
            _fixture = null;
        }
    }

    [After(TestSession)]
    public static Task TearDown() => DisposeAsync();
}
