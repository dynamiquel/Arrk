using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Arrk.Music.Models;

namespace Arrk.Music.Services;

/// <summary>
/// Keyless client for the public Deezer API, Arrk's primary music metadata/browse/artwork
/// provider. Fast CDN artwork, real charts, genres, search, tracklists and previews, no API key.
/// </summary>
public class DeezerService
{
    const string BaseUrl = "https://api.deezer.com/";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        // Deezer uses snake_case (cover_xl, picture_xl, nb_album, track_position, …).
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    const int MaxCacheEntries = 3000;

    // Deezer allows ~50 requests / 5s.
    static readonly SemaphoreSlim Gate = new(6, 6);

    readonly HttpClient _http;
    readonly ILogger<DeezerService>? _logger;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> _cache = new();

    sealed record CacheEntry(DateTime ExpiresAt, string Json);

    public DeezerService(ILogger<DeezerService>? logger = null)
        : this(logger, null)
    {
    }

    public DeezerService(ILogger<DeezerService>? logger, HttpMessageHandler? handler)
    {
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    // Search

    /// <summary>Mixed search across artists, albums and tracks, ranked by Deezer relevance.</summary>
    public async Task<List<MusicItem>> SearchAsync(string query, int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        DeezerList<DeezerItem>? r = await GetAsync<DeezerList<DeezerItem>>(
            $"search?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
        return (r?.Data ?? []).Select(MapMixed).OfType<MusicItem>().ToList();
    }

    public async Task<List<MusicItem>> SearchArtistsAsync(string query, int limit = 30, CancellationToken ct = default)
    {
        DeezerList<DeezerArtist>? r = await GetAsync<DeezerList<DeezerArtist>>(
            $"search/artist?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
        return (r?.Data ?? []).Select(MapArtist).ToList();
    }

    public async Task<List<MusicItem>> SearchAlbumsAsync(string query, int limit = 30, CancellationToken ct = default)
    {
        DeezerList<DeezerAlbum>? r = await GetAsync<DeezerList<DeezerAlbum>>(
            $"search/album?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
        return (r?.Data ?? []).Select(MapAlbum).ToList();
    }

    /// <summary>
    /// Track-only search. Deezer's generic <c>/search</c> endpoint is heavily track-biased and buries
    /// albums/artists, so Arrk searches each entity type explicitly and merges the sections.
    /// </summary>
    public async Task<List<MusicItem>> SearchTracksAsync(string query, int limit = 30, CancellationToken ct = default)
    {
        DeezerList<DeezerTrack>? r = await GetAsync<DeezerList<DeezerTrack>>(
            $"search/track?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
        return (r?.Data ?? []).Select(MapChartTrack).ToList();
    }

    // Playlists (curated / editorial)

    /// <summary>Playlist search, used to resolve the official "Top &lt;Country&gt;" charts.</summary>
    public async Task<List<MusicItem>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        DeezerList<DeezerPlaylist>? r = await GetAsync<DeezerList<DeezerPlaylist>>(
            $"search/playlist?q={Uri.EscapeDataString(query)}&limit={limit}", ct);
        return (r?.Data ?? []).Select(MapPlaylist).ToList();
    }

    /// <summary>Deezer's featured editorial playlists (Top UK, Relaxation, 80s Hits, …). The public
    /// chart endpoint serves at most 100 of them.</summary>
    public async Task<List<MusicItem>> GetChartPlaylistsAsync(int limit = 100, CancellationToken ct = default)
    {
        DeezerList<DeezerPlaylist>? r = await GetAsync<DeezerList<DeezerPlaylist>>($"chart/0/playlists?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapPlaylist).ToList();
    }

    /// <summary>Deezer's editorial playlists for one genre (Pop, Rock, Rap, …), the per-genre playlist
    /// shelves the Deezer site shows. The genre chart endpoint is the only public source for these.</summary>
    public async Task<List<MusicItem>> GetGenrePlaylistsAsync(int genreId, int limit = 100, CancellationToken ct = default)
    {
        DeezerList<DeezerPlaylist>? r = await GetAsync<DeezerList<DeezerPlaylist>>($"chart/{genreId}/playlists?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapPlaylist).ToList();
    }

    /// <summary>Playlist metadata (no tracks).</summary>
    public async Task<MusicItem?> GetPlaylistInfoAsync(long playlistId, CancellationToken ct = default)
    {
        DeezerPlaylist? p = await GetAsync<DeezerPlaylist>($"playlist/{playlistId}", ct);
        return p is null ? null : MapPlaylist(p);
    }

    /// <summary>The tracks in a playlist, mapped like chart tracks (preview + album/artist ids).</summary>
    public async Task<List<MusicItem>> GetPlaylistTracksAsync(long playlistId, int limit = 100, CancellationToken ct = default)
    {
        DeezerList<DeezerTrack>? r = await GetAsync<DeezerList<DeezerTrack>>($"playlist/{playlistId}/tracks?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapChartTrack).ToList();
    }

    // Charts / genres

    public async Task<List<MusicItem>> GetChartTracksAsync(int genreId = 0, int limit = 100, CancellationToken ct = default)
    {
        DeezerList<DeezerTrack>? r = await GetAsync<DeezerList<DeezerTrack>>($"chart/{genreId}/tracks?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapChartTrack).ToList();
    }

    public async Task<List<MusicItem>> GetChartAlbumsAsync(int genreId = 0, int limit = 50, CancellationToken ct = default)
    {
        DeezerList<DeezerAlbum>? r = await GetAsync<DeezerList<DeezerAlbum>>($"chart/{genreId}/albums?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapAlbum).ToList();
    }

    public async Task<List<MusicItem>> GetChartArtistsAsync(int limit = 50, CancellationToken ct = default)
    {
        DeezerList<DeezerArtist>? r = await GetAsync<DeezerList<DeezerArtist>>($"chart/0/artists?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapArtist).ToList();
    }

    public async Task<List<DeezerGenre>> GetGenresAsync(CancellationToken ct = default)
    {
        DeezerList<DeezerGenre>? r = await GetAsync<DeezerList<DeezerGenre>>("genre", ct);
        return (r?.Data ?? []).Where(g => g.Id > 0).ToList();
    }

    // Artwork resolution (name-based, for library/MB items)

    public async Task<string> GetArtistImageAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        DeezerList<DeezerArtist>? r = await GetAsync<DeezerList<DeezerArtist>>(
            $"search/artist?q={Uri.EscapeDataString(name)}&limit=1", ct);
        DeezerArtist? a = r?.Data?.FirstOrDefault();
        return FirstNonEmpty(a?.PictureXl, a?.PictureBig, a?.PictureMedium);
    }

    public async Task<string> GetAlbumCoverAsync(string artist, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string q = Uri.EscapeDataString($"{artist} {title}".Trim());
        DeezerList<DeezerAlbum>? r = await GetAsync<DeezerList<DeezerAlbum>>($"search/album?q={q}&limit=15", ct);
        List<DeezerAlbum> albums = r?.Data ?? [];
        if (albums.Count == 0) return "";

        // Match on the album title rather than taking the first search hit, which can be a different
        // release with a similar name. Punctuation-only titles such as "+" and "=" are kept. Among
        // title matches, prefer the requested artist so a cover by another artist is not chosen.
        string want = AlbumKey(title);
        string wantArtist = ArtistKey(artist);
        DeezerAlbum best = albums[0];
        if (want.Length > 0)
        {
            List<DeezerAlbum> titleMatches = albums.Where(a => AlbumKey(a.Title ?? "") == want).ToList();
            best = (wantArtist.Length > 0
                        ? titleMatches.FirstOrDefault(a => ArtistKey(a.Artist?.Name ?? "") == wantArtist)
                        : null)
                ?? titleMatches.FirstOrDefault()
                ?? albums.FirstOrDefault(a => AlbumKey(a.Title ?? "").StartsWith(want, StringComparison.Ordinal))
                ?? albums.FirstOrDefault(a => AlbumKey(a.Title ?? "").Replace("deluxe", "") == want)
                ?? albums.FirstOrDefault(a => AlbumKey(a.Title ?? "").Contains(want, StringComparison.Ordinal))
                ?? albums[0];
        }
        return FirstNonEmpty(best.CoverXl, best.CoverBig, best.CoverMedium);
    }

    static string ArtistKey(string name) =>
        new((name ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    static string AlbumKey(string value) =>
        new(value.Where(c => char.IsLetterOrDigit(c) || c is '+' or '=' or '÷' or '×' or '#')
            .Select(char.ToLowerInvariant).ToArray());

    // Artist

    public async Task<MusicItem?> GetArtistAsync(long artistId, CancellationToken ct = default)
    {
        DeezerArtist? a = await GetAsync<DeezerArtist>($"artist/{artistId}", ct);
        return a is null ? null : MapArtist(a);
    }

    public async Task<List<MusicItem>> GetArtistTopTracksAsync(long artistId, int limit = 20, CancellationToken ct = default)
    {
        DeezerList<DeezerTrack>? r = await GetAsync<DeezerList<DeezerTrack>>($"artist/{artistId}/top?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapChartTrack).ToList();
    }

    public async Task<List<MusicItem>> GetArtistAlbumsAsync(long artistId, int limit = 50, CancellationToken ct = default)
    {
        DeezerList<DeezerAlbum>? r = await GetAsync<DeezerList<DeezerAlbum>>($"artist/{artistId}/albums?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapAlbum).ToList();
    }

    public async Task<List<MusicItem>> GetArtistRelatedAsync(long artistId, int limit = 20, CancellationToken ct = default)
    {
        DeezerList<DeezerArtist>? r = await GetAsync<DeezerList<DeezerArtist>>($"artist/{artistId}/related?limit={limit}", ct);
        return (r?.Data ?? []).Select(MapArtist).ToList();
    }

    // Album

    public async Task<MusicItem?> GetAlbumAsync(long albumId, CancellationToken ct = default)
    {
        DeezerAlbum? a = await GetAsync<DeezerAlbum>($"album/{albumId}", ct);
        if (a is null) return null;

        DeezerList<DeezerTrack>? tracks = await GetAsync<DeezerList<DeezerTrack>>($"album/{albumId}/tracks?limit=200", ct);
        List<MusicTrackItem> list = MapTracks(tracks?.Data);

        // Some albums' /tracks sub-resource answers "no data" (Deezer error 800) even though the album
        // detail embeds the whole tracklist. Falling back to it keeps the album from being stored with
        // no track rows, a track-less album can never be matched, so a single-song grab that resolves
        // to it loops forever ("did not match the requested song").
        if (list.Count == 0)
            list = MapTracks(a.Tracks?.Data);

        return MapAlbum(a) with { Tracks = list, TrackCount = list.Count };
    }

    static List<MusicTrackItem> MapTracks(List<DeezerTrack>? tracks) =>
        (tracks ?? [])
            .Select(t => new MusicTrackItem
            {
                Number = t.TrackPosition ?? 0,
                DiscNumber = t.DiskNumber is > 0 ? t.DiskNumber.Value : 1,
                Title = t.Title ?? "",
                DurationSeconds = t.Duration,
                DeezerId = t.Id,
                PreviewUrl = t.Preview ?? ""
            })
            .OrderBy(t => t.DiscNumber).ThenBy(t => t.Number)
            .ToList();

    /// <summary>One track by its Deezer id, with its real title, performer and album id. Used to bind
    /// a downloaded file to the exact song a request named, instead of guessing from the file name.</summary>
    public async Task<MusicItem?> GetTrackAsync(long trackId, CancellationToken ct = default)
    {
        if (trackId <= 0)
            return null;
        DeezerTrack? track = await GetAsync<DeezerTrack>($"track/{trackId}", ct);
        return track is null ? null : MapChartTrack(track);
    }

    // Mapping

    static MusicItem MapArtist(DeezerArtist a) => new()
    {
        EntityType = MusicEntityType.Artist,
        Title = a.Name ?? "",
        ArtistName = a.Name ?? "",
        DeezerId = a.Id,
        ArtUrl = FirstNonEmpty(a.PictureXl, a.PictureBig, a.PictureMedium),
        AlbumCount = a.NbAlbum
    };

    static MusicItem MapAlbum(DeezerAlbum a) => new()
    {
        EntityType = MusicEntityType.Album,
        Title = a.Title ?? "",
        ArtistName = a.Artist?.Name ?? "",
        DeezerId = a.Id,
        ArtistDeezerId = a.Artist?.Id ?? 0,
        ArtUrl = FirstNonEmpty(a.CoverXl, a.CoverBig, a.CoverMedium),
        Year = ParseYear(a.ReleaseDate),
        AlbumType = string.IsNullOrWhiteSpace(a.RecordType) ? "Album" : a.RecordType,
        TrackCount = a.NbTracks
    };

    static MusicItem MapPlaylist(DeezerPlaylist p) => new()
    {
        EntityType = MusicEntityType.Playlist,
        Title = p.Title ?? "",
        ArtistName = p.User?.Name ?? "",
        DeezerId = p.Id,
        ArtUrl = FirstNonEmpty(p.PictureXl, p.PictureBig, p.PictureMedium),
        Overview = p.Description ?? "",
        TrackCount = p.NbTracks
    };

    static MusicItem MapChartTrack(DeezerTrack t) => new()
    {
        EntityType = MusicEntityType.Track,
        Title = t.Title ?? "",
        ArtistName = t.Artist?.Name ?? "",
        DeezerId = t.Id,
        ArtistDeezerId = t.Artist?.Id ?? 0,
        AlbumDeezerId = t.Album?.Id ?? 0,
        ArtUrl = FirstNonEmpty(t.Album?.CoverXl, t.Album?.CoverBig, t.Album?.CoverMedium),
        PreviewUrl = t.Preview ?? ""
    };

    static MusicItem? MapMixed(DeezerItem i) => i.Type switch
    {
        "artist" => new MusicItem
        {
            EntityType = MusicEntityType.Artist,
            Title = i.Name ?? "",
            ArtistName = i.Name ?? "",
            DeezerId = i.Id,
            ArtUrl = FirstNonEmpty(i.PictureXl, i.PictureBig, i.PictureMedium)
        },
        "album" => new MusicItem
        {
            EntityType = MusicEntityType.Album,
            Title = i.Title ?? "",
            ArtistName = i.Artist?.Name ?? "",
            DeezerId = i.Id,
            ArtistDeezerId = i.Artist?.Id ?? 0,
            ArtUrl = FirstNonEmpty(i.CoverXl, i.CoverBig, i.CoverMedium)
        },
        "track" => new MusicItem
        {
            EntityType = MusicEntityType.Track,
            Title = i.Title ?? "",
            ArtistName = i.Artist?.Name ?? "",
            DeezerId = i.Id,
            ArtistDeezerId = i.Artist?.Id ?? 0,
            AlbumDeezerId = i.Album?.Id ?? 0,
            ArtUrl = FirstNonEmpty(i.Album?.CoverXl, i.Album?.CoverBig),
            PreviewUrl = i.Preview ?? ""
        },
        _ => null
    };

    static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    static int ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4) return 0;
        return int.TryParse(date[..4], out int year) ? year : 0;
    }

    // HTTP

    async Task<T?> GetAsync<T>(string relative, CancellationToken ct) where T : class
    {
        if (_cache.TryGetValue(relative, out CacheEntry? entry) && entry.ExpiresAt > DateTime.UtcNow)
            return JsonSerializer.Deserialize<T>(entry.Json, JsonOpts);

        string json;
        await Gate.WaitAsync(ct);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(relative, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogDebug("Deezer {Path} returned {Status}", relative, response.StatusCode);
                return null;
            }
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Deezer request {Path} failed", relative);
            return null;
        }
        finally
        {
            Gate.Release();
        }

        if (_cache.Count > MaxCacheEntries)
            _cache.Clear();
        _cache[relative] = new CacheEntry(DateTime.UtcNow + CacheTtl, json);

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger?.LogDebug(ex, "Deezer response could not be parsed");
            return null;
        }
    }

    // DTOs

    class DeezerList<T> { public List<T> Data { get; set; } = []; }

    public sealed class DeezerGenre
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? PictureXl { get; set; }
        public string? PictureBig { get; set; }
    }

    class DeezerArtist
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? PictureXl { get; set; }
        public string? PictureBig { get; set; }
        public string? PictureMedium { get; set; }
        public int NbAlbum { get; set; }
    }

    class DeezerAlbum
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? CoverXl { get; set; }
        public string? CoverBig { get; set; }
        public string? CoverMedium { get; set; }
        public DeezerArtist? Artist { get; set; }
        public string? ReleaseDate { get; set; }
        public string? RecordType { get; set; }
        public int NbTracks { get; set; }
        /// <summary>The album detail embeds the tracklist; used when the /tracks sub-resource refuses
        /// to serve it (Deezer "no data", error 800).</summary>
        public DeezerList<DeezerTrack>? Tracks { get; set; }
    }

    class DeezerTrack
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public int Duration { get; set; }
        public int? TrackPosition { get; set; }
        public int? DiskNumber { get; set; }
        public string? Preview { get; set; }
        public string? Isrc { get; set; }
        public DeezerArtist? Artist { get; set; }
        public DeezerAlbum? Album { get; set; }
    }

    class DeezerPlaylist
    {
        public long Id { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int NbTracks { get; set; }
        public string? PictureXl { get; set; }
        public string? PictureBig { get; set; }
        public string? PictureMedium { get; set; }
        public DeezerArtist? User { get; set; }
    }

    /// <summary>Loose shape covering the mixed /search payload (type = artist|album|track).</summary>
    class DeezerItem
    {
        public string? Type { get; set; }
        public long Id { get; set; }
        public string? Name { get; set; }
        public string? Title { get; set; }
        public string? Preview { get; set; }
        public string? PictureXl { get; set; }
        public string? PictureBig { get; set; }
        public string? PictureMedium { get; set; }
        public string? CoverXl { get; set; }
        public string? CoverBig { get; set; }
        public string? CoverMedium { get; set; }
        public DeezerArtist? Artist { get; set; }
        public DeezerAlbum? Album { get; set; }
    }
}
