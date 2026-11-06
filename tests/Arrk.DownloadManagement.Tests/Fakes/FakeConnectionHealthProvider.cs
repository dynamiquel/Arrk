using Arrk.Core.Services;

namespace Arrk.DownloadManagement.Tests;

/// <summary>
/// Configurable <see cref="IConnectionHealthProvider"/> for download-management tests. Defaults to
/// a healthy, unknown connection so torrent cleanup behaves as before unless a test says otherwise.
/// </summary>
public sealed class FakeConnectionHealthProvider : IConnectionHealthProvider
{
    public ConnectionHealth Health { get; set; } =
        new() { IsHealthy = true, IsKnown = false, Target = "network" };

    public int Calls { get; private set; }

    public Task<ConnectionHealth> GetDownloadConnectionHealthAsync(CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult(Health);
    }
}
