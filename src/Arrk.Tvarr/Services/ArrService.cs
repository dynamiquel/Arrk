using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Services;

public abstract class ArrService
{
    protected abstract ArrClient Client { get; }

    public async Task<int> GetDefaultQualityProfileId(CancellationToken ct = default)
    {
        List<QualityProfile> profiles = await Client.GetQualityProfiles(ct);
        return profiles.FirstOrDefault()?.Id ?? 1;
    }

    public async Task<int?> GetQualityProfileId(string name, CancellationToken ct = default)
    {
        List<QualityProfile> profiles = await Client.GetQualityProfiles(ct);
        return profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public async Task<List<QualityProfile>> GetQualityProfiles(CancellationToken ct = default)
    {
        return await Client.GetQualityProfiles(ct);
    }

    public async Task<List<TagDto>> GetTags(CancellationToken ct = default)
    {
        return await Client.GetTags(ct);
    }

    public abstract Task<string> GetDefaultRootFolder(CancellationToken ct = default);

    public async Task<List<QueueItemDto>> GetQueue(CancellationToken ct = default)
    {
        return await Client.GetQueue(ct);
    }

    public async Task ToggleMonitored(int id, bool monitored, CancellationToken ct = default)
    {
        await ToggleMonitoredCore(id, monitored, ct);
    }

    protected abstract Task ToggleMonitoredCore(int id, bool monitored, CancellationToken ct);

    protected static string ResolveImageUrl(List<ImageDto>? images, string coverType)
    {
        return images?.FirstOrDefault(i => i.CoverType == coverType)?.RemoteUrl
            ?? images?.FirstOrDefault(i => i.CoverType == coverType)?.Url
            ?? string.Empty;
    }
}
