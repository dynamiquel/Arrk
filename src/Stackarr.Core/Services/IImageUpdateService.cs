namespace Stackarr.Core.Services;

public interface IImageUpdateService
{
    Task<ImageUpdateInfo> GetImageUpdateInfoAsync(string image, CancellationToken ct = default);
    Task<List<ImageUpdateInfo>> GetAllImageUpdateInfosAsync(CancellationToken ct = default);
    Task<List<ServiceUpdateInfo>> GetServiceUpdateInfosAsync(CancellationToken ct = default);
    Task<ForceRepullResult> ForceRepullAsync(string image, IProgress<ImagePullProgress>? progress = null, CancellationToken ct = default);
    Task<string?> ResolveDigestAsync(string image, CancellationToken ct = default);
}

public class ImageUpdateInfo
{
    public required string Image { get; init; }
    public string? StoredDigest { get; init; }
    public string? RemoteDigest { get; init; }
    public bool UpdateAvailable => !string.IsNullOrEmpty(StoredDigest) &&
                                   !string.IsNullOrEmpty(RemoteDigest) &&
                                   !string.Equals(StoredDigest, RemoteDigest, StringComparison.OrdinalIgnoreCase);
    public List<string> DependentServices { get; init; } = [];
    public List<string> ExternalContainers { get; init; } = [];
    public bool HasExternalContainers => ExternalContainers.Count > 0;
}

public class ServiceUpdateInfo
{
    public required string ServiceKey { get; init; }
    public required string DisplayName { get; init; }
    public required string Image { get; init; }
    public string? StoredDigest { get; init; }
    public string? RemoteDigest { get; init; }
    public bool UpdateAvailable => !string.IsNullOrEmpty(StoredDigest) &&
                                   !string.IsNullOrEmpty(RemoteDigest) &&
                                   !string.Equals(StoredDigest, RemoteDigest, StringComparison.OrdinalIgnoreCase);
}

public class ForceRepullResult
{
    public bool Success { get; set; }
    public List<string> AffectedServices { get; set; } = [];
    public string? ErrorMessage { get; set; }
}

public class ImagePullProgress
{
    public required string Status { get; init; }
    public string? CurrentStep { get; init; }
}
