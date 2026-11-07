using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Mal;
using Arrk.Mal.Services;
using Arrk.TestSupport;

namespace Arrk.Mal.Tests;

public class MalMappingDatasetTests
{
    [Test]
    public async Task EnsureLoadedAsync_DoesNothingWhenDisabled()
    {
        MalConfig config = new()
        {
            Enabled = false,
            DatasetUrl = "http://127.0.0.1:1/should-not-be-requested"
        };
        MalMappingDataset dataset = new(
            new MutableOptionsMonitor<MalConfig>(config),
            NullLogger<MalMappingDataset>.Instance);

        await dataset.EnsureLoadedAsync();

        await Assert.That(dataset.LoadedAt).IsEqualTo(DateTime.MinValue);
        await Assert.That(dataset.ByMalId(1)).IsNull();
    }
}
