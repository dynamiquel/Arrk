using System.Collections.Concurrent;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Stackarr.Docker;

public class ImageManager(DockerClient client)
{
    static readonly HttpClient SharedHttpClient = new();

    public ConcurrentDictionary<string, byte> ImagesBeingRepulled { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsImageBeingRepulled(string image)
    {
        string imageName = image.Split(':')[0];
        return ImagesBeingRepulled.Keys.Any(key =>
            image.Equals(key, StringComparison.OrdinalIgnoreCase) ||
            imageName.Equals(key.Split(':')[0], StringComparison.OrdinalIgnoreCase));
    }

    public async Task PullImage(string image, CancellationToken ct = default)
    {
        string[] imageParts = image.Split(':');
        string imageName = imageParts[0];
        string tag = imageParts.Length > 1 ? imageParts[1] : "latest";

        ImagesCreateParameters createParams = new()
        {
            FromImage = imageName,
            Tag = tag
        };
        await client.Images.CreateImageAsync(createParams, new(), new Progress<JSONMessage>(), ct);
    }

    public async Task PullImageAsync(
        string image,
        IProgress<JSONMessage> progress,
        CancellationToken ct = default)
    {
        string[] imageParts = image.Split(':');
        string imageName = imageParts[0];
        string tag = imageParts.Length > 1 ? imageParts[1] : "latest";

        ImagesCreateParameters createParams = new()
        {
            FromImage = imageName,
            Tag = tag
        };
        await client.Images.CreateImageAsync(createParams, new(), progress, ct);
    }

    public async Task<bool> ImageExists(string image, CancellationToken ct = default)
    {
        Dictionary<string, IDictionary<string, bool>> filters = new()
        {
            ["reference"] = new Dictionary<string, bool> { [image.Split(':')[0]] = true }
        };

        ImagesListParameters listParams = new()
        {
            Filters = filters
        };
        IList<ImagesListResponse>? images = await client.Images.ListImagesAsync(listParams, ct);

        return images.Any();
    }

    public async Task RemoveImage(string imageId, CancellationToken ct = default)
    {
        ImageDeleteParameters deleteParams = new()
        {
            Force = true
        };
        await client.Images.DeleteImageAsync(imageId, deleteParams, ct);
    }

    public async Task<string?> GetLocalImageDigestAsync(string image, CancellationToken ct = default)
    {
        try
        {
            ImageInspectResponse? inspect = await client.Images.InspectImageAsync(image, ct);
            if (inspect.RepoDigests?.Count > 0)
            {
                string? digestEntry = inspect.RepoDigests.FirstOrDefault(d => d.Contains('@'));
                if (digestEntry != null)
                {
                    int atIdx = digestEntry.IndexOf('@');
                    return digestEntry[(atIdx + 1)..];
                }
            }
            return inspect.ID;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> ResolveDigestAsync(string image, CancellationToken ct = default)
    {
        string? localDigest = await GetLocalImageDigestAsync(image, ct);
        if (localDigest != null)
            return localDigest;

        await PullImage(image, ct);
        return await GetLocalImageDigestAsync(image, ct);
    }

    public static async Task<string?> GetRemoteDigestAsync(string image, CancellationToken ct = default)
    {
        try
        {
            (string registry, string repository, string tag) = ParseImageReference(image);
            string? token = await GetRegistryTokenAsync(registry, repository, ct);

            string url = $"https://{registry}/v2/{repository}/manifests/{tag}";
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept",
                "application/vnd.docker.distribution.manifest.v2+json, " +
                "application/vnd.docker.distribution.manifest.list.v2+json, " +
                "application/vnd.oci.image.manifest.v1+json, " +
                "application/vnd.oci.image.index.v1+json");

            if (token != null)
                request.Headers.Authorization = new("Bearer", token);

            using HttpResponseMessage response = await SharedHttpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode &&
                response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? digestValues))
            {
                return digestValues.FirstOrDefault();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> IsUpdateAvailableAsync(string image, CancellationToken ct = default)
    {
        string? localDigest = await GetLocalImageDigestFromClientAsync(image, ct);
        string? remoteDigest = await GetRemoteDigestAsync(image, ct);

        if (localDigest == null || remoteDigest == null)
            return false;

        return !string.Equals(localDigest, remoteDigest, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<string?> GetLocalImageDigestFromClientAsync(string image, CancellationToken ct)
    {
        try
        {
            using DockerClient client = DockerClientFactory.Create();
            ImageInspectResponse? inspect = await client.Images.InspectImageAsync(image, ct);
            if (inspect.RepoDigests?.Count > 0)
            {
                string? digestEntry = inspect.RepoDigests.FirstOrDefault(d => d.Contains('@'));
                if (digestEntry != null)
                {
                    int atIdx = digestEntry.IndexOf('@');
                    return digestEntry[(atIdx + 1)..];
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    static (string registry, string repository, string tag) ParseImageReference(string image)
    {
        string[] parts = image.Split(':');
        string name = parts[0];
        string tag = parts.Length > 1 ? parts[1] : "latest";

        string registry;
        string repository;

        if (name.Contains('.'))
        {
            int slashIdx = name.IndexOf('/');
            registry = name[..slashIdx];
            repository = name[(slashIdx + 1)..];
        }
        else
        {
            registry = "registry-1.docker.io";
            repository = name.Contains('/') ? name : $"library/{name}";
        }

        registry = registry.ToLowerInvariant() switch
        {
            "docker.io" or "hub.docker.com" => "registry-1.docker.io",
            _ => registry
        };

        return (registry, repository, tag);
    }

    static async Task<string?> GetRegistryTokenAsync(
        string registry,
        string repository,
        CancellationToken ct)
    {
        if (registry is "registry-1.docker.io")
        {
            string scope = $"repository:{repository}:pull";
            string tokenUrl = $"https://auth.docker.io/token?service=registry.docker.io&scope={scope}";
            string response = await SharedHttpClient.GetStringAsync(tokenUrl, ct);
            using JsonDocument doc = JsonDocument.Parse(response);
            return doc.RootElement.GetProperty("token").GetString();
        }

        return null;
    }

    public static TimeSpan GetImageAge(DateTime createdAt)
    {
        return DateTime.UtcNow - createdAt;
    }

    public static string GetAgeDescription(TimeSpan age)
    {
        if (age.TotalDays < 1)
            return $"{(int)age.TotalHours}h ago";
        if (age.TotalDays < 30)
            return $"{(int)age.TotalDays}d ago";
        if (age.TotalDays < 365)
            return $"{(int)(age.TotalDays / 30)}mo ago";
        return $"{(int)(age.TotalDays / 365)}y ago";
    }

    public static bool IsImageOutdated(TimeSpan age)
    {
        return age.TotalDays > 30;
    }
}
