using System.Globalization;
using Microsoft.Extensions.Logging;
using Stackarr.Ai;
using Stackarr.Core.Models;
using Stackarr.Music.Models;
using Stackarr.Music.Services;

namespace Stackarr.Music.Services;

/// <summary>
/// Exposes the native music library plus Deezer discovery to the assistant. Ids are opaque and
/// encode the scope (<c>lib</c> for Stackarr rows, <c>dz</c> for Deezer metadata) so a library album
/// and a Deezer album with the same numeric id can never be confused.
///
/// <para>
/// Search covers the local library first (including unidentified rows that Deezer cannot find), then
/// discovery charts/search, and applies genre, year, scope and sort filters. That makes "what jazz
/// do I have" and "surprise me with something new" the same tool call as a title search.
/// </para>
/// </summary>
public sealed class MusicAssistantMediaSource(
    MusicCatalogService catalog,
    ILogger<MusicAssistantMediaSource> logger) : IAssistantMediaSource
{
    /// <summary>Stable id prefix shared by the instance property and the static helpers.</summary>
    public const string SourceKey = "music";

    public string Key => SourceKey;
    public string DisplayName => "Music";

    public bool IsAvailable => true;

    public async Task<IReadOnlyList<AssistantMediaSummary>> SearchAsync(
        AssistantMediaQuery query, CancellationToken ct)
    {
        if (!WantsMusic(query.Domain))
            return [];

        // Music has no anime concept; an anime-only request must not return an arbitrary album.
        if (query.Anime == true)
            return [];

        int limit = Math.Clamp(query.Limit <= 0 ? 8 : query.Limit, 1, 25);
        List<(MusicItem Item, bool Library)> candidates = [];

        if (query.Scope is AssistantMediaScope.Library or AssistantMediaScope.Any)
            await AddLibraryAsync(candidates, query, ct);
        if (query.Scope is AssistantMediaScope.Discovery or AssistantMediaScope.Any)
            await AddDiscoveryAsync(candidates, query, ct);

        return candidates
            .Where(c => MatchesYear(c.Item, query))
            .GroupBy(c => (MusicAssistantFilters.Normalize(c.Item.Title), MusicAssistantFilters.Normalize(c.Item.ArtistName)))
            .Select(g => g.OrderByDescending(c => c.Library).First())
            .Where(c => query.Scope != AssistantMediaScope.Discovery || !c.Library)
            .Where(c => query.Scope != AssistantMediaScope.Library || c.Library)
            .OrderByDescending(c => c.Library)
            .ThenBy(c => SortKey(c.Item, query.Sort))
            .Select(c => ToSummary(c.Item, c.Library))
            .Where(s => s is not null)
            .Select(s => s!)
            .Take(limit)
            .ToList();
    }

    //  Library

    async Task AddLibraryAsync(
        List<(MusicItem Item, bool Library)> candidates,
        AssistantMediaQuery query,
        CancellationToken ct)
    {
        (List<MusicItem> artists, List<MusicItem> albums) view;
        try
        {
            view = await catalog.GetLibraryViewAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Assistant music library view failed");
            return;
        }

        Dictionary<string, List<string>> genresByArtist = view.artists
            .GroupBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Genres, StringComparer.OrdinalIgnoreCase);

        foreach (MusicItem album in view.albums)
        {
            IReadOnlyList<string> genres = genresByArtist.GetValueOrDefault(album.ArtistName, []);
            if (!MatchesAlbum(album, genres, query))
                continue;
            candidates.Add((album, true));
        }

        foreach (MusicItem artist in view.artists)
        {
            if (!MatchesArtist(artist, query))
                continue;
            candidates.Add((artist, true));
        }

        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            try
            {
                List<MusicItem> songs = await catalog.GetLibrarySongsAsync(ct);
                foreach (MusicItem song in songs)
                {
                    if (!MusicAssistantFilters.MatchesText(query.Text, song.Title, song.ArtistName, song.AlbumName))
                        continue;
                    candidates.Add((song, true));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Assistant music song search failed");
            }
        }
    }

    static bool MatchesAlbum(
        MusicItem album, IReadOnlyList<string> artistGenres, AssistantMediaQuery query)
    {
        if (!MusicAssistantFilters.MatchesGenre(artistGenres.Concat(album.Genres), query.Genre))
            return false;
        return MusicAssistantFilters.MatchesText(query.Text, album.Title, album.ArtistName, album.AlbumName);
    }

    static bool MatchesArtist(MusicItem artist, AssistantMediaQuery query)
    {
        if (!MusicAssistantFilters.MatchesGenre(artist.Genres, query.Genre))
            return false;
        return MusicAssistantFilters.MatchesText(query.Text, artist.Title, artist.ArtistName);
    }

    //  Discovery

    async Task AddDiscoveryAsync(
        List<(MusicItem Item, bool Library)> candidates,
        AssistantMediaQuery query,
        CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                MusicSearchResult result = await catalog.SearchAsync(query.Text, ct);
                candidates.AddRange(result.Artists.Select(i => (i, false)));
                candidates.AddRange(result.Albums.Select(i => (i, false)));
                candidates.AddRange(result.Tracks.Select(i => (i, false)));
            }
            else if (!string.IsNullOrWhiteSpace(query.Genre))
            {
                List<DeezerService.DeezerGenre> genres = await catalog.GetGenresAsync(ct);
                DeezerService.DeezerGenre? match = genres.FirstOrDefault(g =>
                    !string.IsNullOrWhiteSpace(g.Name)
                    && g.Name.Contains(query.Genre.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    List<MusicItem> albums = await catalog.GetGenreAlbumsAsync(match.Id, ct);
                    List<MusicItem> tracks = await catalog.GetGenreTracksAsync(match.Id, ct);
                    candidates.AddRange(albums.Select(i => (i, false)));
                    candidates.AddRange(tracks.Select(i => (i, false)));
                }
            }
            else
            {
                List<MusicItem> albums = await catalog.GetChartAlbumsAsync(ct);
                List<MusicItem> tracks = await catalog.GetChartTracksAsync(ct);
                candidates.AddRange(albums.Select(i => (i, false)));
                candidates.AddRange(tracks.Select(i => (i, false)));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Assistant music discovery failed");
        }
    }

    static bool MatchesYear(MusicItem item, AssistantMediaQuery query)
    {
        int year = item.Year;
        if (query.YearFrom is not null && year > 0 && year < query.YearFrom)
            return false;
        if (query.YearTo is not null && year > 0 && year > query.YearTo)
            return false;
        return true;
    }

    static string SortKey(MusicItem item, string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        "title" => item.Title,
        "newest" or "year" => (9999 - Math.Clamp(item.Year, 0, 9999)).ToString("D4", CultureInfo.InvariantCulture),
        _ => item.Title
    };

    //  Detail

    public async Task<AssistantMediaDetail?> GetAsync(string id, string? username, CancellationToken ct)
    {
        string[] parts = id.Split(':');
        if (parts.Length < 4 || !string.Equals(parts[0], Key, StringComparison.OrdinalIgnoreCase))
            return null;

        string kind = parts[1].ToLowerInvariant();
        string scope = parts[2].ToLowerInvariant();
        if (!int.TryParse(parts[3], out int entityId) || entityId <= 0)
            return null;

        try
        {
            return kind switch
            {
                "album" => await AlbumDetailAsync(scope, entityId, ct),
                "artist" => await ArtistDetailAsync(scope, entityId, ct),
                "track" when parts.Length >= 5 && int.TryParse(parts[4], out int trackRef)
                    => await TrackDetailAsync(scope, entityId, trackRef, ct),
                _ => null
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Assistant music detail failed for {Id}", id);
            return null;
        }
    }

    async Task<AssistantMediaDetail?> AlbumDetailAsync(string scope, int albumId, CancellationToken ct)
    {
        MusicItem? album = scope == "lib"
            ? await catalog.GetLibraryAlbumAsync(albumId, ct)
            : await catalog.GetAlbumAsync(albumId, ct);
        if (album is null)
            return null;

        List<string> facts = [];
        AddIf(facts, "Artist", album.ArtistName);
        AddIf(facts, "Year", album.Year > 0 ? album.Year.ToString(CultureInfo.InvariantCulture) : null);
        AddIf(facts, "Type", album.AlbumType);
        AddIf(facts, "Library status", string.IsNullOrWhiteSpace(album.LibraryStatus) ? null : album.LibraryStatus);
        AddIf(facts, "Tracks", album.TrackCount > 0
            ? $"{album.TrackFileCount}/{album.TrackCount} downloaded"
            : null);
        AddIf(facts, "Format", album.FormatLabel);
        AddIf(facts, "Size on disk", FormatSize(album.SizeOnDisk));
        AddIf(facts, "Genres", album.Genres.Count > 0 ? string.Join(", ", album.Genres) : null);

        string albumScope = scope;
        List<AssistantMediaSummary> tracks = album.Tracks
            .OrderBy(t => t.DiscNumber)
            .ThenBy(t => t.Number)
            .Select(t =>
            {
                string reference = albumScope == "lib" && t.TrackId > 0
                    ? t.TrackId.ToString(CultureInfo.InvariantCulture)
                    : t.Number.ToString(CultureInfo.InvariantCulture);
                string status = albumScope == "lib"
                    ? (t.HasFile ? "Downloaded" : t.Monitored ? "Missing" : "Not monitored")
                    : "Not in library";
                return new AssistantMediaSummary(SourceKey,
                    $"music:track:{albumScope}:{albumId}:{reference}",
                    AssistantMediaKind.Track,
                    $"{t.Number}. {t.Title}",
                    album.ArtistName,
                    null,
                    status);
            })
            .ToList();

        AssistantMediaSummary summary = new(Key, $"music:album:{albumScope}:{albumId}",
            AssistantMediaKind.Album, album.Title, album.ArtistName, album.Year > 0 ? album.Year : null,
            string.IsNullOrWhiteSpace(album.LibraryStatus) ? "Not in library" : album.LibraryStatus,
            album.ArtUrl);
        return new AssistantMediaDetail(summary, album.Overview, facts, tracks);
    }

    async Task<AssistantMediaDetail?> ArtistDetailAsync(string scope, int artistId, CancellationToken ct)
    {
        MusicItem? artist;
        List<MusicItem> albums;

        if (scope == "lib")
        {
            (List<Artist> artists, _) = await catalog.GetLibraryAsync(ct);
            Artist? row = artists.FirstOrDefault(a => a.Id == artistId);
            if (row is null)
                return null;

            artist = MusicItem.FromArtist(row);
            albums = await catalog.GetLibraryArtistAlbumsAsync(artistId, ct);
        }
        else
        {
            artist = await catalog.GetArtistAsync(artistId, ct);
            if (artist is null)
                return null;

            albums = await catalog.GetArtistAlbumsAsync(artistId, ct);
        }

        List<string> facts = [];
        AddIf(facts, "Library status", string.IsNullOrWhiteSpace(artist.LibraryStatus) ? null : artist.LibraryStatus);
        AddIf(facts, "Albums", artist.AlbumCount > 0 ? artist.AlbumCount.ToString(CultureInfo.InvariantCulture) : null);
        AddIf(facts, "Tracks", artist.TrackCount > 0
            ? $"{artist.TrackFileCount}/{artist.TrackCount} downloaded"
            : null);
        AddIf(facts, "Genres", artist.Genres.Count > 0 ? string.Join(", ", artist.Genres) : null);
        AddIf(facts, "Listeners", artist.Listeners is > 0
            ? artist.Listeners.Value.ToString("N0", CultureInfo.InvariantCulture)
            : null);
        AddIf(facts, "Size on disk", FormatSize(artist.SizeOnDisk));

        List<AssistantMediaSummary> related = albums
            .Select(a =>
            {
                string albumScope = a.LibraryId > 0 ? "lib" : "dz";
                int albumId = a.LibraryId > 0 ? a.LibraryId : (int)a.DeezerId;
                return new AssistantMediaSummary(SourceKey, $"music:album:{albumScope}:{albumId}",
                    AssistantMediaKind.Album, a.Title, artist.Title,
                    a.Year > 0 ? a.Year : null,
                    string.IsNullOrWhiteSpace(a.LibraryStatus) ? "Not in library" : a.LibraryStatus,
                    a.ArtUrl);
            })
            .Where(a => !a.Id.EndsWith(":0", StringComparison.Ordinal))
            .ToList();

        AssistantMediaSummary summary = new(Key, $"music:artist:{scope}:{artistId}",
            AssistantMediaKind.Artist, artist.Title, "Artist", null,
            string.IsNullOrWhiteSpace(artist.LibraryStatus) ? "Not in library" : artist.LibraryStatus,
            artist.ArtUrl);
        return new AssistantMediaDetail(summary, artist.Overview, facts, related);
    }

    async Task<AssistantMediaDetail?> TrackDetailAsync(
        string scope, int albumId, int trackRef, CancellationToken ct)
    {
        MusicItem? album = scope == "lib"
            ? await catalog.GetLibraryAlbumAsync(albumId, ct)
            : await catalog.GetAlbumAsync(albumId, ct);
        if (album is null)
            return null;

        MusicTrackItem? track = scope == "lib"
            ? album.Tracks.FirstOrDefault(t => t.TrackId == trackRef)
            : album.Tracks.FirstOrDefault(t => t.Number == trackRef);
        if (track is null)
            return null;

        List<string> facts =
        [
            $"Artist: {album.ArtistName}",
            $"Album: {album.Title}",
            $"Track: {track.Number}"
        ];
        AddIf(facts, "Duration", track.DurationSeconds > 0
            ? TimeSpan.FromSeconds(track.DurationSeconds).ToString(@"m\:ss")
            : null);
        facts.Add("Downloaded: " + (scope == "lib" ? (track.HasFile ? "yes" : "no") : "not in library"));
        if (scope == "lib")
            facts.Add("Monitored: " + (track.Monitored ? "yes" : "no"));

        AssistantMediaSummary summary = new(Key,
            $"music:track:{scope}:{albumId}:{trackRef}", AssistantMediaKind.Track,
            track.Title, album.ArtistName, album.Year > 0 ? album.Year : null,
            scope == "lib" ? (track.HasFile ? "Downloaded" : "Missing") : "Not in library",
            album.ArtUrl);
        return new AssistantMediaDetail(summary, "", facts, []);
    }

    //  Mapping

    static AssistantMediaSummary? ToSummary(MusicItem item, bool library)
    {
        return item.EntityType switch
        {
            MusicEntityType.Artist => Artist(item, library),
            MusicEntityType.Track when library => LibraryTrack(item),
            _ => Album(item, library)
        };
    }

    static AssistantMediaSummary? Artist(MusicItem item, bool library)
    {
        int id = library && item.LibraryId > 0 ? item.LibraryId : (int)item.DeezerId;
        if (id <= 0)
            return null;

        return new AssistantMediaSummary(SourceKey,
            $"{SourceKey}:artist:{(library ? "lib" : "dz")}:{id}",
            AssistantMediaKind.Artist, item.Title, "Artist", null, Status(item, library), item.ArtUrl);
    }

    static AssistantMediaSummary? Album(MusicItem item, bool library)
    {
        int id = library && item.LibraryId > 0 ? item.LibraryId : (int)item.DeezerId;
        if (id <= 0)
            return null;

        return new AssistantMediaSummary(SourceKey,
            $"{SourceKey}:album:{(library ? "lib" : "dz")}:{id}",
            AssistantMediaKind.Album, item.Title, item.ArtistName,
            item.Year > 0 ? item.Year : null, Status(item, library), item.ArtUrl);
    }

    static AssistantMediaSummary? LibraryTrack(MusicItem item)
    {
        if (item.LibraryId <= 0 || item.Id <= 0)
            return null;

        return new AssistantMediaSummary(SourceKey,
            $"{SourceKey}:track:lib:{item.LibraryId}:{item.Id}",
            AssistantMediaKind.Track, item.Title, item.ArtistName,
            null,
            item.HasFile ? "Downloaded" : item.IsMonitored ? "Missing" : "Not monitored",
            item.ArtUrl);
    }

    static string Status(MusicItem item, bool library) =>
        !string.IsNullOrWhiteSpace(item.LibraryStatus)
            ? item.LibraryStatus
            : library || item.IsInLibrary ? "In library" : "Not in library";

    static bool WantsMusic(string? domain) =>
        string.IsNullOrWhiteSpace(domain)
        || domain.Contains("music", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("album", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("artist", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("track", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("song", StringComparison.OrdinalIgnoreCase)
        || domain.Contains("playlist", StringComparison.OrdinalIgnoreCase);

    static void AddIf(List<string> facts, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            facts.Add($"{label}: {value}");
    }

    static string? FormatSize(long bytes) => bytes switch
    {
        <= 0 => null,
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB"
    };
}
