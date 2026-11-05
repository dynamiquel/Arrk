using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.Docker;

namespace Stackarr.Orchestration;

public class ImageUpdateService(
    ContainerManager containerManager,
    ImageManager imageManager,
    ResourceInventory inventory,
    ServiceRegistry serviceRegistry,
    ServiceProvisioner provisioner,
    IOptionsMonitor<StackarrConfig> configMonitor,
    IConfiguration configuration,
    StackarrInstance stackarr,
    ILogger<ImageUpdateService> logger)
    : IImageUpdateService
{
    public async Task<ImageUpdateInfo> GetImageUpdateInfoAsync(string image, CancellationToken ct = default)
    {
        List<ContainerInfo> allContainers = await containerManager.GetContainersByImageAsync(image, ct);

        List<ContainerInfo> managedContainers = allContainers.Where(c =>
            c.Labels.TryGetValue(ContainerLabels.Managed, out string? m) && m == "true"
        ).ToList();

        List<ContainerInfo> externalContainers = allContainers.Where(c =>
            !c.Labels.TryGetValue(ContainerLabels.Managed, out string? m) || m != "true"
        ).ToList();

        List<string> dependentServices = new();
        string? storedDigest = null;

        foreach (ContainerInfo container in managedContainers)
        {
            if (container.Labels.TryGetValue(ContainerLabels.Service, out string? serviceKey))
            {
                string displayName = serviceRegistry.Get(serviceKey)?.Definition.DisplayName ?? serviceKey;
                if (!dependentServices.Contains(displayName))
                    dependentServices.Add(displayName);
            }

            if (storedDigest == null)
                storedDigest = ExtractDigestFromImageRef(container.Image);
        }

        string? remoteDigest = await ImageManager.GetRemoteDigestAsync(image, ct);

        return new()
        {
            Image = image,
            StoredDigest = storedDigest,
            RemoteDigest = remoteDigest,
            DependentServices = dependentServices,
            ExternalContainers = externalContainers.Select(c => c.Name).ToList()
        };
    }

    public async Task<List<ImageUpdateInfo>> GetAllImageUpdateInfosAsync(CancellationToken ct = default)
    {
        List<ContainerInfo> owned = await inventory.GetOwnedContainers(ct);
        List<string> images = owned.Select(c => c.Image).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        List<ImageUpdateInfo> results = new();
        foreach (string image in images)
        {
            try
            {
                ImageUpdateInfo info = await GetImageUpdateInfoAsync(image, ct);
                results.Add(info);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get update info for image {Image}", image);
            }
        }
        return results;
    }

    public async Task<List<ServiceUpdateInfo>> GetServiceUpdateInfosAsync(CancellationToken ct = default)
    {
        StackarrConfig config = configMonitor.CurrentValue;
        List<string> enabledServices = ConfigSyncer.GetEnabledServices(configuration, serviceRegistry);
        List<ContainerInfo> allOwned = await inventory.GetOwnedContainers(ct);
        List<ServiceUpdateInfo> results = new();
        HashSet<string> processedImages = new(StringComparer.OrdinalIgnoreCase);

        foreach (string serviceKey in enabledServices)
        {
            Service? service = serviceRegistry.Get(serviceKey);
            if (service == null)
                continue;

            List<ContainerInfo> containers = allOwned.Where(c =>
                c.Labels.TryGetValue(ContainerLabels.Service, out string? s) &&
                s.Equals(serviceKey, StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (containers.Count == 0)
                continue;

            ContainerInfo container = containers[0];
            if (!processedImages.Add(container.Image))
                continue;

            VolumePathResolver volumeResolver = new(config.Storage);
            string? imageTag = service.Definition.BuildContainerSpecs(configuration, volumeResolver, stackarr).FirstOrDefault()?.Image;
            if (imageTag == null)
                continue;

            string? storedDigest = ExtractDigestFromImageRef(container.Image);
            string? remoteDigest = null;

            try
            {
                remoteDigest = await ImageManager.GetRemoteDigestAsync(imageTag, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to get remote digest for {Image}", imageTag);
            }

            results.Add(new()
            {
                ServiceKey = serviceKey,
                DisplayName = service.Definition.DisplayName,
                Image = container.Image,
                StoredDigest = storedDigest,
                RemoteDigest = remoteDigest
            });
        }

        return results;
    }

    public async Task<ForceRepullResult> ForceRepullAsync(string image, IProgress<ImagePullProgress>? progress = null, CancellationToken ct = default)
    {
        ForceRepullResult result = new();

        try
        {
            progress?.Report(new() { Status = "Checking image dependencies...", CurrentStep = "analyzing" });

            ImageUpdateInfo updateInfo = await GetImageUpdateInfoAsync(image, ct);

            if (updateInfo.HasExternalContainers)
            {
                string names = string.Join(", ", updateInfo.ExternalContainers);
                result.ErrorMessage = $"Cannot repull: image is in use by external container(s): {names}";
                return result;
            }

            result.AffectedServices = updateInfo.DependentServices;

            progress?.Report(new() { Status = $"Stopping {updateInfo.DependentServices.Count} service(s)...", CurrentStep = "stopping" });

            List<ContainerInfo> allContainers = await containerManager.GetContainersByImageAsync(image, ct);
            List<ContainerInfo> managedContainers = allContainers.Where(c =>
                c.Labels.TryGetValue(ContainerLabels.Managed, out string? m) && m == "true"
            ).ToList();

            foreach (ContainerInfo container in managedContainers)
            {
                try
                {
                    if (container.State == Core.Enums.ContainerState.Running)
                    {
                        logger.LogInformation(
                            "Stopping container {Name} for image {Image}",
                            container.Name, image);
                        await containerManager.StopContainer(container.Id);
                    }
                    logger.LogInformation("Removing container {Name}", container.Name);
                    await containerManager.RemoveContainer(container.Id, force: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to remove container {Name}", container.Name);
                }
            }

            progress?.Report(new() { Status = "Removing old image...", CurrentStep = "removing" });

            try
            {
                await imageManager.RemoveImage(image);
                logger.LogInformation("Removed old image {Image}", image);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to remove old image {Image}", image);
            }

            progress?.Report(new() { Status = "Pulling new image...", CurrentStep = "pulling" });

            await imageManager.PullImageAsync(image, new Progress<global::Docker.DotNet.Models.JSONMessage>(msg =>
            {
                if (!string.IsNullOrEmpty(msg.Status))
                {
                    progress?.Report(new() { Status = msg.Status, CurrentStep = "pulling" });
                }
            }), ct);

            progress?.Report(new() { Status = "Restarting services...", CurrentStep = "restarting" });

            StackarrConfig config = configMonitor.CurrentValue;
            VolumePathResolver volumeResolver = new(config.Storage);

            foreach (ContainerInfo container in managedContainers)
            {
                if (!container.Labels.TryGetValue(ContainerLabels.Service, out string? serviceKey))
                    continue;

                Service? service = serviceRegistry.Get(serviceKey);
                if (service == null || !service.Definition.IsEnabled(configuration))
                    continue;

                try
                {
                    IReadOnlyList<ContainerSpec> specs = service.Definition.BuildContainerSpecs(configuration, volumeResolver, stackarr);
                    foreach (ContainerSpec spec in specs)
                    {
                        logger.LogInformation(
                            "Re-provisioning service {Service} ({Container})",
                            serviceKey, spec.Name);
                        await provisioner.ProvisionContainer(spec, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to re-provision service {Service}", serviceKey);
                }
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to force repull image {Image}", image);
            result.ErrorMessage = ex.Message;
        }

        return result;
    }

    public async Task<string?> ResolveDigestAsync(string image, CancellationToken ct = default)
    {
        return await imageManager.ResolveDigestAsync(image, ct);
    }

    static string? ExtractDigestFromImageRef(string imageRef)
    {
        int atIndex = imageRef.IndexOf('@');
        if (atIndex < 0)
            return null;
        string digest = imageRef[(atIndex + 1)..];
        return digest.StartsWith("sha256:") ? digest : null;
    }
}
