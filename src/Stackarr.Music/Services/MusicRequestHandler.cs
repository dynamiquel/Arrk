using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Enums;
using Stackarr.Core.Requests;
using Stackarr.Music.Models;
using Stackarr.Music.Services;

namespace Stackarr.Music;

/// <summary>
/// Contributes the Music request capability, backed by Stackarr's own library and acquisition;
/// no Lidarr. Adding a request records library intent and (when asked) starts a native grab.
/// Requests carry the canonical Deezer artist/album ids (Deezer is the identity provider).
/// </summary>
public sealed class MusicRequestHandler(
    MusicLibrary library,
    MusicCatalogService catalog,
    MusicAcquisitionService acquisition,
    IOptionsMonitor<MusicConfig> config,
    ILogger<MusicRequestHandler> logger) : IMediaRequestHandler
{
    public string Key => "music";
    public string DisplayName => "Music";
    public bool IsAvailable => config.CurrentValue.Enabled;

    public IReadOnlyList<RequestCapability> Capabilities { get; } =
    [
        new(MusicMediaDomains.Music, RequestKind.Artist),
        new(MusicMediaDomains.Music, RequestKind.Album),
        new(MusicMediaDomains.Music, RequestKind.Track),
    ];

    public Task<IReadOnlyList<RequestQualityOption>> GetQualityOptionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RequestQualityOption>>(
        [
            new("0", "Best Available"),
            new("1", "Lossless"),
            new("2", "Lossy"),
        ]);

    public async Task<RequestTargetState> CheckAsync(RequestTarget target, CancellationToken ct = default)
    {
        try
        {
            return target.Kind switch
            {
                RequestKind.Artist => await CheckArtistAsync(target, ct),
                RequestKind.Album or RequestKind.Track => await CheckAlbumAsync(target, ct),
                _ => RequestTargetState.Missing()
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Music request check failed for {Title}", target.Title);
            return RequestTargetState.Missing();
        }
    }

    async Task<RequestTargetState> CheckArtistAsync(RequestTarget target, CancellationToken ct)
    {
        long deezerId = target.GetProviderIdInt(DeezerKeys.Artist);
        Artist? artist =
            (deezerId > 0 ? await library.FindArtistByDeezerIdAsync(deezerId, ct) : null)
            ?? await library.FindArtistByNameAsync(target.Title, ct);
        return artist is null
            ? RequestTargetState.Missing()
            : RequestTargetState.InLibrary(artist.Id.ToString(), artist.HasAnyTracks);
    }

    async Task<RequestTargetState> CheckAlbumAsync(RequestTarget target, CancellationToken ct)
    {
        long deezerId = target.GetProviderIdInt(DeezerKeys.Album);
        Album? album =
            (deezerId > 0 ? await library.FindAlbumByDeezerIdAsync(deezerId, ct) : null)
            ?? await library.FindAlbumByTitleAsync(null, target.Title, ct);
        return album is null
            ? RequestTargetState.Missing()
            : RequestTargetState.InLibrary(album.Id.ToString(), album.TrackFileCount > 0);
    }

    public async Task<FulfilmentResult> FulfilAsync(FulfilmentRequest request, CancellationToken ct = default)
    {
        RequestTarget target = request.Target;
        try
        {
            int? profileId = ParseProfileId(request.QualityKey);
            return target.Kind switch
            {
                RequestKind.Artist => await FulfilArtistAsync(target, profileId, request.SearchNow, ct),
                RequestKind.Album => await FulfilAlbumAsync(target, profileId, request.SearchNow, ct),
                RequestKind.Track => await FulfilAlbumAsync(target, profileId, request.SearchNow, ct),
                _ => FulfilmentResult.Fail($"Unsupported music request kind {target.Kind}.")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Music request fulfilment failed for {Title}", target.Title);
            return FulfilmentResult.Fail(ex.Message);
        }
    }

    async Task<FulfilmentResult> FulfilArtistAsync(RequestTarget target, int? profileId, bool searchNow, CancellationToken ct)
    {
        long deezerId = target.GetProviderIdInt(DeezerKeys.Artist);
        Artist? existing =
            (deezerId > 0 ? await library.FindArtistByDeezerIdAsync(deezerId, ct) : null)
            ?? await library.FindArtistByNameAsync(target.Title, ct);
        if (existing is not null)
            return FulfilmentResult.Ok(existing.Id.ToString(), "Artist already in library.");

        MusicItem? deezer = await catalog.FindArtistByNameAsync(target.Title, ct);
        MusicItem item = deezer ?? new MusicItem
        {
            EntityType = MusicEntityType.Artist,
            Title = target.Title,
            ArtistName = target.Title
        };

        await catalog.AddArtistToLibraryAsync(item, profileId, ct);

        Artist? added = item.DeezerId > 0
            ? await library.FindArtistByDeezerIdAsync(item.DeezerId, ct)
            : await library.FindArtistByNameAsync(target.Title, ct);
        return added is null
            ? FulfilmentResult.Ok("", $"Added {target.Title}.")
            : FulfilmentResult.Ok(added.Id.ToString(), $"Added {target.Title}.");
    }

    async Task<FulfilmentResult> FulfilAlbumAsync(RequestTarget target, int? profileId, bool searchNow, CancellationToken ct)
    {
        long deezerId = target.GetProviderIdInt(DeezerKeys.Album);

        MusicItem? deezer = (deezerId > 0 ? await catalog.GetAlbumAsync(deezerId, ct) : null)
            ?? await catalog.FindAlbumByNameAsync(null, target.Title, ct);
        if (deezer is null)
            return FulfilmentResult.Fail($"Could not find \"{target.Title}\" on Deezer.");

        await catalog.AddAlbumToLibraryAsync(deezer, profileId, ct);

        Album? added =
            (deezer.DeezerId > 0 ? await library.FindAlbumByDeezerIdAsync(deezer.DeezerId, ct) : null)
            ?? await library.FindAlbumByTitleAsync(deezer.ArtistName, deezer.Title, ct);

        if (added is null)
            return FulfilmentResult.Fail($"Album {target.Title} could not be added.");

        if (searchNow && added.TrackFileCount < added.TotalTrackCount)
            await AcquireAsync(added.ArtistName, added.Title, ct);

        return FulfilmentResult.Ok(added.Id.ToString(), $"Added {target.Title}.");
    }

    /// <summary>Hands the grab to Stackarr's native acquisition (title-first torrents then Soulseek).</summary>
    async Task AcquireAsync(string artistName, string albumTitle, CancellationToken ct)
    {
        try
        {
            await acquisition.SearchAndGrabAsync(artistName, albumTitle, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Acquisition failed for {Album}", albumTitle);
        }
    }

    static int? ParseProfileId(string? key) => int.TryParse(key, out int id) ? id : null;

    /// <summary>Provider-id keys for music requests (mirrored from the Requests module).</summary>
    public static class DeezerKeys
    {
        public const string Album = "deezer";
        public const string Artist = "deezerartist";
    }
}
