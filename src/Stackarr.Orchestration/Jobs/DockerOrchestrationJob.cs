using Hangfire;
using Microsoft.Extensions.Configuration;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Core.Services;

namespace Stackarr.Orchestration.Jobs;

public class DockerOrchestrationJob(
    ServiceProvisioner provisioner,
    ServiceRegistry serviceRegistry,
    StackarrInstance stackarr,
    IConfiguration configuration)
{
    public const string DefaultCron = "* * * * *";

    [JobDisplayName("Docker Orchestration")]
    public async Task Execute(CancellationToken ct)
    {
        List<string> enabledKeys =
            ConfigSyncer.GetEnabledServices(
                configuration, serviceRegistry);
        VolumePathResolver volumeResolver = new(new());

        foreach (string key in enabledKeys)
        {
            Service? service = serviceRegistry.Get(key);
            if (service == null)
            {
                continue;
            }

            IReadOnlyList<ContainerSpec> specs =
                service.Definition.BuildContainerSpecs(
                    configuration, volumeResolver, stackarr);
            foreach (ContainerSpec spec in specs)
            {
                if (!await provisioner.EnsureContainerRunning(
                        spec.Name, ct))
                {
                    await provisioner.ProvisionContainer(
                        spec, ct);
                }
            }
        }
    }
}
