using Microsoft.Extensions.Configuration;
using Stackarr.Core.Configuration;
using Stackarr.Core.Services;

namespace Stackarr.Music;

/// <summary>
/// Resolves the music module's storage roots. Kept out of the services that use them so the lyrics
/// job, the importer and the acquisition service all agree on where the library lives.
/// </summary>
internal static class MusicPaths
{
    /// <summary>Library root as seen by this process (e.g. <c>/media/music</c> in the shipped image).</summary>
    public static string ResolveLibraryRoot(IConfiguration configuration) =>
        Resolve(configuration, RelativeVolumes.Music);

    /// <summary>Root of the downloads tree as seen by this process.</summary>
    public static string ResolveDownloadsRoot(IConfiguration configuration) =>
        Resolve(configuration, RelativeVolumes.Downloads);

    static string Resolve(IConfiguration configuration, string relativeVolume)
    {
        StorageConfig storage = configuration.GetSection("storage").Get<StorageConfig>() ?? new StorageConfig();
        return new VolumePathResolver(storage).ResolveToContainerPath(relativeVolume);
    }
}
