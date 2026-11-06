using Microsoft.Extensions.Logging;
using Arrk.Ai;
using Arrk.Music.Models;

namespace Arrk.Music.Services;

/// <summary>
/// Deterministic music recommendations: Deezer charts, genre charts and related-artist rows, with
/// everything the user already has removed. Chart position and related-artist proximity become the
/// score, so "surprise me with a banger" is bounded and cheap instead of a model guess.
/// </summary>
public sealed class MusicAssistantRecommendationSource(
    MusicCatalogService catalog,
    ILogger<MusicAssistantRecommendationSource> logger) : IAssistantRecommendationSource
{
    public string Key => MusicAssistantMediaSource.SourceKey;

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<AssistantRecommendation>> RecommendAsync(
        AssistantRecommendationQuery query, CancellationToken ct)
    {
        if (query.Anime == true)
            return [];

        if (!string.IsNullOrWhiteSpace(query.Domain)
            && !query.Domain.Contains("music", StringComparison.OrdinalIgnoreCase)
            && !query.Domain.Contains("album", StringComparison.OrdinalIgnoreCase)
            && !query.Domain.Contains("artist", StringComparison.OrdinalIgnoreCase)
            && !query.Domain.Contains("song", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        int limit = Math.Clamp(query.Limit <= 0 ? 6 : query.Limit, 1, 15);
        List<(MusicItem Album, string Reason, double Score)> candidates = [];

        if (!string.IsNullOrWhiteSpace(query.Genre))
            await AddGenreAsync(candidates, query.Genre, ct);
        else
            await AddChartsAsync(candidates, ct);

        await AddRelatedAsync(candidates, ct);

        return candidates
            .Where(c => c.Album.DeezerId > 0 || c.Album.LibraryId > 0)
            .Where(c => !c.Album.IsInLibrary)
            .GroupBy(c => (Normalize(c.Album.Title), Normalize(c.Album.ArtistName)))
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .Take(limit)
            .Select(c => new AssistantRecommendation(
                ToSummary(c.Album), c.Reason, c.Score))
            .ToList();
    }

    async Task AddChartsAsync(
        List<(MusicItem Album, string Reason, double Score)> candidates, CancellationToken ct)
    {
        List<MusicItem> albums = await SafeAsync(() => catalog.GetChartAlbumsAsync(ct), []);
        for (int i = 0; i < albums.Count; i++)
            candidates.Add((albums[i], "Charting now", 100 - i));

        List<MusicItem> tracks = await SafeAsync(() => catalog.GetChartTracksAsync(ct), []);
        for (int i = 0; i < tracks.Count; i++)
        {
            MusicItem? album = AsAlbum(tracks[i]);
            if (album is not null)
                candidates.Add((album, "Charting now", 90 - i * 0.5));
        }
    }

    async Task AddGenreAsync(
        List<(MusicItem Album, string Reason, double Score)> candidates,
        string genre, CancellationToken ct)
    {
        List<DeezerService.DeezerGenre> genres = await SafeAsync(() => catalog.GetGenresAsync(ct), []);
        DeezerService.DeezerGenre? match = genres.FirstOrDefault(g =>
            !string.IsNullOrWhiteSpace(g.Name)
            && g.Name.Contains(genre.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            await AddChartsAsync(candidates, ct);
            return;
        }

        List<MusicItem> albums = await SafeAsync(() => catalog.GetGenreAlbumsAsync(match.Id, ct), []);
        for (int i = 0; i < albums.Count; i++)
            candidates.Add((albums[i], $"Popular in {match.Name}", 100 - i));

        List<MusicItem> tracks = await SafeAsync(() => catalog.GetGenreTracksAsync(match.Id, ct), []);
        for (int i = 0; i < tracks.Count; i++)
        {
            MusicItem? album = AsAlbum(tracks[i]);
            if (album is not null)
                candidates.Add((album, $"Popular in {match.Name}", 90 - i * 0.5));
        }
    }

    async Task AddRelatedAsync(
        List<(MusicItem Album, string Reason, double Score)> candidates, CancellationToken ct)
    {
        (List<MusicItem> artists, _) = await SafeAsync(() => catalog.GetLibraryViewAsync(ct), ([], []));
        foreach (MusicItem seed in artists
                     .Where(a => a.DeezerId > 0)
                     .OrderByDescending(a => a.TrackFileCount)
                     .Take(3))
        {
            List<MusicItem> related = await SafeAsync(
                () => catalog.GetArtistRelatedAsync(seed.DeezerId, ct), []);
            foreach (MusicItem artist in related
                         .Where(a => a.DeezerId > 0 && !a.IsInLibrary)
                         .Take(3))
            {
                List<MusicItem> albums = await SafeAsync(
                    () => catalog.GetArtistAlbumsAsync(artist.DeezerId, ct), []);
                foreach (MusicItem album in albums.Where(a => a.DeezerId > 0).Take(2))
                {
                    candidates.Add((
                        album,
                        $"More like {seed.Title}",
                        70 + (album.Rating ?? 0)));
                }
            }
        }
    }

    static MusicItem? AsAlbum(MusicItem track)
    {
        if (track.EntityType != MusicEntityType.Track)
            return track;

        // Chart/search tracks carry their album's Deezer id rather than a library album id.
        if (track.AlbumDeezerId <= 0 && track.DeezerId <= 0)
            return null;

        return track with
        {
            EntityType = MusicEntityType.Album,
            Id = 0,
            LibraryId = 0,
            DeezerId = track.AlbumDeezerId,
            Title = string.IsNullOrWhiteSpace(track.AlbumName) ? track.Title : track.AlbumName,
            IsInLibrary = false
        };
    }

    static AssistantMediaSummary ToSummary(MusicItem album) => new(
        MusicAssistantMediaSource.SourceKey,
        $"{MusicAssistantMediaSource.SourceKey}:album:dz:{album.DeezerId}",
        AssistantMediaKind.Album,
        album.Title,
        album.ArtistName,
        album.Year > 0 ? album.Year : null,
        "Not in library",
        album.ArtUrl);

    static string Normalize(string? value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    async Task<T> SafeAsync<T>(Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Assistant music recommendation lookup failed");
            return fallback;
        }
    }
}
