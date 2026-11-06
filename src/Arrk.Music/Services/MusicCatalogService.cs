using Hangfire;
using Microsoft.Extensions.Logging;
using Arrk.Core.Models;
using Arrk.Music.Models;
using Arrk.Navidrome;

namespace Arrk.Music.Services;

/// <summary>
/// The music discovery/curation layer, powered entirely by Deezer (search, charts, genres,
/// artist/album detail, artwork, previews, and the canonical artist/album/track ids). Library
/// membership and intent come from Arrk's own <see cref="MusicLibrary"/> (Navidrome is the
/// player).
/// </summary>
public class MusicCatalogService
{
    static readonly TimeSpan LibraryCacheTtl = TimeSpan.FromSeconds(30);

    readonly DeezerService _deezer;
    readonly MusicLibrary _library;
    readonly MusicAcquisitionService _acquisition;
    readonly MusicNotifications? _notifications;
    readonly Hangfire.IBackgroundJobClient? _backgroundJobs;
    readonly Microsoft.Extensions.Options.IOptionsMonitor<MusicConfig>? _musicConfig;
    readonly ILogger<MusicCatalogService> _logger;
    readonly Arrk.Music.Lyrics.LyricsService? _lyrics;

    readonly Lock _gate = new();
    DateTime _libraryCacheAt = DateTime.MinValue;
    HashSet<long> _libraryArtistDeezerIds = [];
    HashSet<string> _libraryArtistNames = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, LibraryAlbumRef> _libraryAlbumByKey = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<long, LibraryAlbumRef> _libraryAlbumByDeezer = [];
    DateTime _albumIndexAt = DateTime.MinValue;
    List<MusicItem>? _songsCache;
    DateTime _songsCacheAt = DateTime.MinValue;
    (List<MusicItem> Artists, List<MusicItem> Albums)? _libraryViewCache;
    DateTime _libraryViewAt = DateTime.MinValue;

    public MusicCatalogService(
        DeezerService deezer,
        MusicLibrary library,
        MusicAcquisitionService acquisition,
        ILogger<MusicCatalogService> logger,
        MusicNotifications? notifications = null,
        Hangfire.IBackgroundJobClient? backgroundJobs = null,
        Microsoft.Extensions.Options.IOptionsMonitor<MusicConfig>? musicConfig = null,
        Arrk.Music.Lyrics.LyricsService? lyrics = null)
    {
        _deezer = deezer;
        _library = library;
        _acquisition = acquisition;
        _logger = logger;
        _notifications = notifications;
        _backgroundJobs = backgroundJobs;
        _musicConfig = musicConfig;
        _lyrics = lyrics;
    }

    // Artwork (library items only; Deezer items carry their own)

    public static string ArtistArtUrl(string name) =>
        string.IsNullOrWhiteSpace(name) ? "" : $"/api/music/art/artist?name={Uri.EscapeDataString(name)}";

    public static string AlbumArtUrl(string artist, string title) =>
        string.IsNullOrWhiteSpace(title) ? "" : $"/api/music/art/album?artist={Uri.EscapeDataString(artist)}&title={Uri.EscapeDataString(title)}";

    /// <summary>Cover art for a track. Play history stores the player's already-resolved cover URL,
    /// while library tracks carry a bare Navidrome cover-art id; accept both and fall back to the
    /// cached album art when there is no cover at all.</summary>
    public static string TrackArtUrl(string artist, string album, string coverArt)
    {
        if (!string.IsNullOrWhiteSpace(coverArt))
            return IsResolvedUrl(coverArt) ? coverArt : NavidromeService.CoverArtUrl(coverArt);

        return AlbumArtUrl(artist, album);
    }

    static bool IsResolvedUrl(string value) =>
        value.StartsWith('/')
        || value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Album art: prefer the cover captured from Deezer when the album was added.</summary>
    static string ArtFor(Album album) =>
        !string.IsNullOrEmpty(album.PosterUrl) ? album.PosterUrl : AlbumArtUrl(album.ArtistName, album.Title);

    public static IReadOnlyList<MusicCountry> Countries { get; } =
    [
        new("global", "Global", ""),
        new("us", "United States", "USA"), new("gb", "United Kingdom", "UK"),
        new("ca", "Canada", "Canada"), new("au", "Australia", "Australia"),
        new("ie", "Ireland", "Ireland"), new("de", "Germany", "Germany"),
        new("fr", "France", "France"), new("es", "Spain", "Spain"),
        new("it", "Italy", "Italy"), new("nl", "Netherlands", "Netherlands"),
        new("be", "Belgium", "Belgium"), new("se", "Sweden", "Sweden"),
        new("no", "Norway", "Norway"), new("dk", "Denmark", "Denmark"),
        new("fi", "Finland", "Finland"), new("pl", "Poland", "Poland"),
        new("pt", "Portugal", "Portugal"), new("ch", "Switzerland", "Switzerland"),
        new("at", "Austria", "Austria"), new("cz", "Czechia", "Czech Republic"),
        new("br", "Brazil", "Brazil"), new("mx", "Mexico", "Mexico"),
        new("ar", "Argentina", "Argentina"), new("cl", "Chile", "Chile"),
        new("co", "Colombia", "Colombia"), new("jp", "Japan", "Japan"),
        new("kr", "South Korea", "South Korea"), new("id", "Indonesia", "Indonesia"),
        new("sg", "Singapore", "Singapore"), new("za", "South Africa", "South Africa"),
        new("tr", "Turkey", "Turkey"), new("ua", "Ukraine", "Ukraine")
    ];

    // Search

    public async Task<MusicSearchResult> SearchAsync(string query, CancellationToken ct = default)
    {
        string q = query?.Trim() ?? "";
        if (q.Length < 2) return MusicSearchResult.Empty;

        Task<List<MusicItem>> artistsTask = _deezer.SearchArtistsAsync(q, 24, ct);
        Task<List<MusicItem>> albumsTask = _deezer.SearchAlbumsAsync(q, 24, ct);
        Task<List<MusicItem>> tracksTask = _deezer.SearchTracksAsync(q, 24, ct);
        Task<List<MusicItem>> playlistsTask = _deezer.SearchPlaylistsAsync(q, 12, ct);
        await Task.WhenAll(artistsTask, albumsTask, tracksTask, playlistsTask);

        List<MusicItem> artists = await AnnotateAsync(await artistsTask, ct);
        List<MusicItem> albums = await AnnotateAsync(await albumsTask, ct);
        List<MusicItem> tracks = await AnnotateAsync(await tracksTask, ct);
        // Editorial playlists carry no library membership (they are the provider's curation), so they
        // are surfaced as-is rather than annotated against the library.
        List<MusicItem> playlists = await playlistsTask;

        return new MusicSearchResult(PickTopResult(q, albums, artists), artists, albums, tracks, playlists);
    }

    static MusicItem? PickTopResult(string query, List<MusicItem> albums, List<MusicItem> artists)
    {
        string q = Normalize(query);
        if (q.Length == 0) return null;
        MusicItem? album = albums.FirstOrDefault();
        if (album is not null && IsTopMatch(album.Title, q)) return album;
        MusicItem? artist = artists.FirstOrDefault();
        if (artist is not null && IsTopMatch(artist.Title, q)) return artist;
        return null;
    }

    static bool IsTopMatch(string title, string normalizedQuery)
    {
        string n = Normalize(title);
        return n == normalizedQuery || n.StartsWith(normalizedQuery, StringComparison.Ordinal);
    }

    static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // Charts / Top 100 / genres

    public async Task<List<MusicItem>> GetChartTracksAsync(CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetChartTracksAsync(0, 100, ct), ct);

    public async Task<List<MusicItem>> GetChartAlbumsAsync(CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetChartAlbumsAsync(0, 50, ct), ct);

    public async Task<List<MusicItem>> GetChartArtistsAsync(CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetChartArtistsAsync(50, ct), ct);

    public Task<List<DeezerService.DeezerGenre>> GetGenresAsync(CancellationToken ct = default) =>
        _deezer.GetGenresAsync(ct);

    public async Task<List<MusicItem>> GetGenreTracksAsync(int genreId, CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetChartTracksAsync(genreId, 100, ct), ct);

    public async Task<List<MusicItem>> GetGenreAlbumsAsync(int genreId, CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetChartAlbumsAsync(genreId, 50, ct), ct);

    // Country charts (Deezer editorial playlists)

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _countryChartCache = new(StringComparer.OrdinalIgnoreCase);

    async Task<long?> ResolveCountryChartAsync(MusicCountry country, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(country.DeezerName)) return null;
        if (_countryChartCache.TryGetValue(country.Code, out long cached)) return cached;
        try
        {
            string term = $"Top {country.DeezerName}";
            List<MusicItem> found = await _deezer.SearchPlaylistsAsync(term, 20, ct);
            MusicItem? match = found.FirstOrDefault(p =>
                IsOfficialChart(p) && string.Equals(p.Title, term, StringComparison.OrdinalIgnoreCase));
            if (match is { DeezerId: > 0 })
            {
                _countryChartCache[country.Code] = match.DeezerId;
                return match.DeezerId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not resolve the {Country} chart playlist", country.Code);
        }
        return null;
    }

    static bool IsOfficialChart(MusicItem playlist) =>
        playlist.EntityType == MusicEntityType.Playlist &&
        !string.IsNullOrEmpty(playlist.Title) &&
        string.Equals(playlist.ArtistName, "Deezer Charts", StringComparison.OrdinalIgnoreCase) &&
        playlist.TrackCount >= 10;

    public async Task<List<MusicItem>> GetCountryChartTracksAsync(string countryCode, CancellationToken ct = default)
    {
        MusicCountry? country = Countries.FirstOrDefault(c => string.Equals(c.Code, countryCode, StringComparison.OrdinalIgnoreCase));
        if (country is null || string.IsNullOrEmpty(country.DeezerName))
            return await GetChartTracksAsync(ct);

        long? playlistId = await ResolveCountryChartAsync(country, ct);
        if (playlistId is null) return await GetChartTracksAsync(ct);

        List<MusicItem> tracks = await _deezer.GetPlaylistTracksAsync(playlistId.Value, 100, ct);
        return tracks.Count == 0 ? await GetChartTracksAsync(ct) : await AnnotateAsync(tracks, ct);
    }

    // Playlists (Deezer editorial)

    public Task<List<MusicItem>> GetChartPlaylistsAsync(int limit = 100, CancellationToken ct = default) =>
        _deezer.GetChartPlaylistsAsync(limit, ct);

    /// <summary>Deezer's editorial playlists for one genre, the per-genre playlist shelf.</summary>
    public Task<List<MusicItem>> GetGenrePlaylistsAsync(int genreId, int limit = 100, CancellationToken ct = default) =>
        _deezer.GetGenrePlaylistsAsync(genreId, limit, ct);

    public Task<MusicItem?> GetPlaylistInfoAsync(long playlistId, CancellationToken ct = default) =>
        _deezer.GetPlaylistInfoAsync(playlistId, ct);

    public async Task<List<MusicItem>> GetPlaylistTracksAsync(long playlistId, int limit = 100, CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetPlaylistTracksAsync(playlistId, limit, ct), ct);

    // Artist

    public async Task<MusicItem?> GetArtistAsync(long artistId, CancellationToken ct = default)
    {
        MusicItem? artist = await _deezer.GetArtistAsync(artistId, ct);
        if (artist is null) return null;
        return await AnnotateArtistAsync(artist, ct);
    }

    public async Task<List<MusicItem>> GetArtistTopTracksAsync(long artistId, CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetArtistTopTracksAsync(artistId, 20, ct), ct);

    public async Task<List<MusicItem>> GetArtistAlbumsAsync(long artistId, CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetArtistAlbumsAsync(artistId, 50, ct), ct);

    public async Task<List<MusicItem>> GetArtistRelatedAsync(long artistId, CancellationToken ct = default) =>
        await AnnotateAsync(await _deezer.GetArtistRelatedAsync(artistId, 20, ct), ct);

    public async Task<MusicItem?> FindArtistByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        List<MusicItem> artists = await _deezer.SearchArtistsAsync(name, 1, ct);
        return artists.FirstOrDefault();
    }

    public async Task<MusicItem?> FindAlbumByNameAsync(string? artist, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        List<MusicItem> albums = await _deezer.SearchAlbumsAsync($"{artist} {title}".Trim(), 5, ct);
        return albums.FirstOrDefault(a => string.Equals(a.Title, title, StringComparison.OrdinalIgnoreCase)) ?? albums.FirstOrDefault();
    }

    /// <summary>Finds an album we actually hold by artist + title (the local library, not Deezer).
    /// Tries the raw title then a cleaned one, so "Hotel California (40th Anniversary...)" matches.</summary>
    public async Task<MusicItem?> FindLibraryAlbumByNameAsync(string? artist, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        foreach (string candidate in new[] { title, MusicText.CleanReleaseName(title) }
            .Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Album? album = await _library.FindAlbumByTitleAsync(artist, candidate, ct);
            if (album is not null)
            {
                Dictionary<int, AlbumGrabState> grabs = await _acquisition.GetAlbumGrabStatesAsync(ct);
                return AnnotateAlbumStatus(album, grabs.GetValueOrDefault(album.Id));
            }
        }
        return null;
    }

    /// <summary>Finds an artist we hold by name (the local library, not Deezer).</summary>
    public async Task<MusicItem?> FindLibraryArtistByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        Artist? artist = await _library.FindArtistByNameAsync(name, ct);
        if (artist is null) return null;
        Dictionary<int, AlbumGrabState> grabs = await _acquisition.GetAlbumGrabStatesAsync(ct);
        return AnnotateArtistStatus(artist, grabs.Values.FirstOrDefault(g => g.Name.Contains(artist.Name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Finds the album we hold that contains a track with this title (used when a song has
    /// no album tag, so the mini-player can still open the right album).</summary>
    public async Task<MusicItem?> FindLibraryAlbumByTrackAsync(string? artist, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string artistKey = Normalize(artist ?? "");
        List<Album> albums = await _library.GetAllAlbumsAsync(ct);
        Album? match = albums.FirstOrDefault(a => a.Tracks.Any(t =>
            MusicText.TitleMatches(t.Title, title) && (artistKey.Length == 0 || Normalize(a.ArtistName) == artistKey)))
            ?? albums.FirstOrDefault(a => a.Tracks.Any(t => MusicText.TitleMatches(t.Title, title)));
        if (match is null) return null;
        Dictionary<int, AlbumGrabState> grabs = await _acquisition.GetAlbumGrabStatesAsync(ct);
        return AnnotateAlbumStatus(match, grabs.GetValueOrDefault(match.Id));
    }

    // Album

    public async Task<MusicItem?> GetAlbumAsync(long albumId, CancellationToken ct = default)
    {
        MusicItem? album = await _deezer.GetAlbumAsync(albumId, ct);
        if (album is null)
            return null;
        (Dictionary<string, LibraryAlbumRef> byKey, Dictionary<long, LibraryAlbumRef> byDeezer) = await GetLibraryAlbumIndexAsync(ct);
        MusicItem annotated = AnnotateAlbum(album, byKey, byDeezer);
        // When we already hold the album, fold in our own track rows (ids, files, identification) so
        // the popup and the Songs tab read from the same source of truth, plus each song's live
        // download state and the album's own grab progress.
        if (annotated.LibraryId > 0)
        {
            annotated = await MergeLibraryTracksAsync(annotated, annotated.LibraryId, ct);
            AlbumGrabState? grab = (await _acquisition.GetAlbumGrabStatesAsync(ct)).GetValueOrDefault(annotated.LibraryId);
            if (grab is not null)
                annotated = annotated with
                {
                    IsDownloading = grab is { Placed: false, IsComplete: false },
                    DownloadSize = grab.Size,
                    DownloadSizeLeft = grab.Remaining,
                    DownloadStatus = grab is { Placed: false } ? $"Grabbing ({grab.Percent}%)" : ""
                };
            annotated = await AttachTrackDownloadsAsync(annotated, annotated.LibraryId, grab, ct);
        }
        return annotated;
    }

    /// <summary>Overlays our library's per-track state (id, file, Deezer id, identification) onto a
    /// metadata tracklist, matching by disc+number then title.</summary>
    async Task<MusicItem> MergeLibraryTracksAsync(MusicItem album, int libraryId, CancellationToken ct)
    {
        Album? lib;
        try
        {
            lib = await _library.GetAlbumAsync(libraryId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load library album {AlbumId} to merge tracks", libraryId);
            return album;
        }
        if (lib is null)
            return album;

        List<MusicTrackItem> merged = [];
        foreach (MusicTrackItem track in album.Tracks)
        {
            Track? match =
                lib.Tracks.FirstOrDefault(x => x.MediumNumber == track.DiscNumber && (x.AbsoluteTrackNumber == track.Number || x.TrackNumber == track.Number))
                ?? lib.Tracks.FirstOrDefault(x => Normalize(x.Title) == Normalize(track.Title));
            merged.Add(match is null
                ? track
                : track with
                {
                    TrackId = match.Id,
                    FilePath = match.FilePath,
                    HasFile = match.HasFile,
                    Monitored = match.Monitored,
                    DeezerId = track.DeezerId > 0 ? track.DeezerId : match.DeezerId,
                    IsUnidentified = match.IsUnidentified
                });
        }
        return album with
        {
            Tracks = merged,
            IsUnidentified = lib.DeezerId <= 0
        };
    }

    // Library

    public async Task<(List<Artist> Artists, List<Album> Albums)> GetLibraryAsync(CancellationToken ct = default)
    {
        try
        {
            return (await _library.GetAllArtistsAsync(ct), await _library.GetAllAlbumsAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load the music library");
            return ([], []);
        }
    }

    public async Task<(List<MusicItem> Artists, List<MusicItem> Albums)> GetLibraryViewAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_libraryViewCache is { } cached && DateTime.UtcNow - _libraryViewAt < LibraryCacheTtl)
                return cached;
        }

        (List<Artist> artists, List<Album> albums) = await GetLibraryAsync(ct);
        Dictionary<int, AlbumGrabState> grabs = await _acquisition.GetAlbumGrabStatesAsync(ct);

        List<MusicItem> albumItems = albums
            .Where(a => (a.Monitored || a.TrackFileCount > 0) && artists.Any(x => x.Id == a.ArtistId))
            .Select(a => AnnotateAlbumStatus(a, grabs.GetValueOrDefault(a.Id)))
            .ToList();

        // Show the format badge (Lossless / codec) on downloaded albums.
        List<MusicItem> downloaded = albumItems.Where(a => a.TrackFileCount > 0).Take(60).ToList();
        if (downloaded.Count > 0)
        {
            System.Collections.Concurrent.ConcurrentDictionary<int, MusicTechnical> techs = new();
            await Parallel.ForEachAsync(downloaded,
                new ParallelOptions { MaxDegreeOfParallelism = 5, CancellationToken = ct },
                async (item, token) =>
                {
                    MusicTechnical tech = await _acquisition.GetAlbumTechnicalAsync(item.LibraryId, token);
                    if (tech.HasInfo)
                        techs[item.LibraryId] = tech;
                });
            albumItems = albumItems.Select(a => techs.TryGetValue(a.LibraryId, out MusicTechnical? tech)
                ? a with { Technical = tech, FormatLabel = tech.Badge }
                : a).ToList();
        }

        HashSet<string> albumArtists = albumItems.Select(a => a.ArtistName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<MusicItem> artistItems = artists
            .Where(a => albumArtists.Contains(a.Name) || a.TrackFileCount > 0)
            .Select(a => AnnotateArtistStatus(a, grabs.Values.FirstOrDefault(g => g.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        lock (_gate)
        {
            _libraryViewCache = (artistItems, albumItems);
            _libraryViewAt = DateTime.UtcNow;
        }
        return (artistItems, albumItems);
    }

    static MusicItem AnnotateAlbumStatus(Album a, AlbumGrabState? grab)
    {
        bool downloadActive = grab is { Placed: false, IsComplete: false };
        bool placed = grab?.Placed == true;
        bool complete = a.TotalTrackCount > 0 && a.TrackFileCount >= a.TotalTrackCount;
        bool hasFiles = placed || a.TrackFileCount > 0;
        // Missing only when the album is actually wanted (monitored). An album we only hold a song
        // from is Partial (grouped with Downloaded); one we never asked for is Unmonitored.
        string status = downloadActive ? "Downloading"
            : complete || a.HasFile ? "Downloaded"
            : hasFiles ? "Partial"
            : a.Monitored ? "Missing"
            : "Unmonitored";

        return MusicItem.FromAlbum(a) with
        {
            ArtUrl = ArtFor(a),
            IsDownloading = downloadActive,
            DownloadSize = grab?.Size ?? 0,
            DownloadSizeLeft = grab?.Remaining ?? 0,
            DownloadStatus = grab is { Placed: false } ? $"Grabbing ({grab.Percent}%)" : "",
            HasFile = placed || a.HasFile,
            LibraryStatus = status
        };
    }

    static MusicItem AnnotateArtistStatus(Artist a, AlbumGrabState? grab)
    {
        bool downloading = grab is not null;
        bool hasFiles = a.TrackFileCount > 0;
        string status = downloading ? "Downloading"
            : a.TrackCount > 0 && hasFiles && a.TrackFileCount >= a.TrackCount ? "Downloaded"
            : hasFiles ? "Partial"
            : a.Monitored ? "Missing"
            : "Unmonitored";
        MusicItem item = MusicItem.FromArtist(a);
        item = item with { ArtUrl = string.IsNullOrEmpty(a.PosterUrl) ? ArtistArtUrl(a.Name) : a.PosterUrl };
        return item with
        {
            IsDownloading = downloading,
            LibraryStatus = status
        };
    }

    public async Task<MusicItem?> GetLibraryAlbumAsync(int albumId, CancellationToken ct = default)
    {
        Album? a = await _library.GetAlbumAsync(albumId, ct);
        if (a is null)
            return null;
        Dictionary<int, AlbumGrabState> grabs = await _acquisition.GetAlbumGrabStatesAsync(ct);
        MusicItem item = AnnotateAlbumStatus(a, grabs.GetValueOrDefault(albumId));
        item = await AttachTrackDownloadsAsync(item, albumId, grabs.GetValueOrDefault(albumId), ct);
        // Add Deezer 30s previews (for missing-track preview buttons) when we know the album's id.
        if (a.DeezerId > 0)
            item = await MergePreviewsAsync(item, a.DeezerId, ct);
        return item;
    }

    /// <summary>
    /// Overlays each track's own in-flight grab (bound by Deezer track id), falling back to the
    /// album's grab for a whole-album torrent, so the album popup's track rows can show progress.
    /// </summary>
    async Task<MusicItem> AttachTrackDownloadsAsync(MusicItem album, int albumId, AlbumGrabState? albumGrab, CancellationToken ct)
    {
        if (album.Tracks.Count == 0)
            return album;

        Dictionary<long, TrackGrabState> trackGrabs;
        try
        {
            trackGrabs = await _acquisition.GetTrackGrabStatesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read track grab states for album {AlbumId}", albumId);
            return album;
        }

        bool albumDownloading = albumGrab is { Placed: false, IsComplete: false };
        List<MusicTrackItem> tracks = album.Tracks.Select(t =>
        {
            if (t.HasFile)
                return t;
            if (t.DeezerId > 0 && trackGrabs.TryGetValue(t.DeezerId, out TrackGrabState? trackGrab) && trackGrab.IsDownloading)
                return t with { IsDownloading = true, DownloadSize = trackGrab.Size, DownloadSizeLeft = trackGrab.Remaining };
            if (albumDownloading && albumGrab is not null)
                return t with { IsDownloading = true, DownloadSize = albumGrab.Size, DownloadSizeLeft = albumGrab.Remaining };
            return t;
        }).ToList();
        return album with { Tracks = tracks };
    }

    /// <summary>Per-requested-track download state, for the playlist popup's progress rows.</summary>
    public Task<Dictionary<long, TrackGrabState>> GetTrackDownloadStatesAsync(CancellationToken ct = default) =>
        _acquisition.GetTrackGrabStatesAsync(ct);

    /// <summary>Adds Deezer preview URLs onto a library album's tracks (matched by disc+number then title).</summary>
    async Task<MusicItem> MergePreviewsAsync(MusicItem album, long deezerId, CancellationToken ct)
    {
        try
        {
            MusicItem? detail = await _deezer.GetAlbumAsync(deezerId, ct);
            if (detail is null || detail.Tracks.Count == 0)
                return album;
            List<MusicTrackItem> merged = album.Tracks.Select(t =>
            {
                MusicTrackItem? d = detail.Tracks.FirstOrDefault(x => x.DiscNumber == t.DiscNumber && x.Number == t.Number)
                    ?? detail.Tracks.FirstOrDefault(x => Normalize(x.Title) == Normalize(t.Title));
                return d is null || string.IsNullOrEmpty(d.PreviewUrl) ? t : t with { PreviewUrl = d.PreviewUrl };
            }).ToList();
            return album with { Tracks = merged };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not merge Deezer previews for album {DeezerId}", deezerId);
            return album;
        }
    }

    /// <summary>
    /// Albums we hold with files, newest first, the library-backed "Recently Added" (never
    /// Navidrome's filesystem view, so it matches the Albums/Songs/Artists tabs exactly).
    /// </summary>
    public async Task<List<MusicItem>> GetRecentlyAddedAsync(int limit = 30, CancellationToken ct = default)
    {
        List<Album> albums = await _library.GetRecentlyAddedAlbumsAsync(limit, ct);
        Dictionary<int, AlbumGrabState> grabs = await _acquisition.GetAlbumGrabStatesAsync(ct);
        return albums.Select(a => AnnotateAlbumStatus(a, grabs.GetValueOrDefault(a.Id))).ToList();
    }

    public async Task<List<MusicItem>> GetLibrarySongsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_songsCache is not null && DateTime.UtcNow - _songsCacheAt < TimeSpan.FromSeconds(60))
                return _songsCache;
        }

        List<MusicItem> songs = [];
        try
        {
            List<Album> tracked = (await _library.GetAllAlbumsAsync(ct))
                .Where(a => a.Monitored || a.TrackFileCount > 0)
                .ToList();

            // Download state for the songs (their own grab, or the album torrent covering them).
            Dictionary<long, TrackGrabState> trackGrabs = await _acquisition.GetTrackGrabStatesAsync(ct);
            Dictionary<int, AlbumGrabState> albumGrabs = await _acquisition.GetAlbumGrabStatesAsync(ct);

            foreach (Album album in tracked)
            {
                if (ct.IsCancellationRequested) break;
                List<Track> tracks = await _library.GetTracksAsync(album.Id, ct);
                // Placed songs, plus the songs we actually asked for. A track we never requested (the
                // rest of an album we only grabbed one song from) is unmonitored, so it is not listed
                // as a missing song, it shows as Unmonitored inside the album popup instead.
                foreach (Track t in tracks.Where(t => t.HasFile || t.Monitored))
                {
                    (bool downloading, long size, long left) = SongDownloadState(t, album.Id, trackGrabs, albumGrabs);
                    songs.Add(new MusicItem
                    {
                        EntityType = MusicEntityType.Track,
                        Id = t.Id,
                        Title = t.Title,
                        ArtistName = album.ArtistName,
                        AlbumName = album.Title,
                        DeezerId = t.DeezerId,
                        ArtistDeezerId = album.ArtistDeezerId,
                        AlbumDeezerId = album.DeezerId,
                        LibraryId = album.Id,
                        ArtUrl = ArtFor(album),
                        IsInLibrary = true,
                        HasFile = t.HasFile,
                        IsMonitored = t.Monitored,
                        IsUnidentified = t.IsUnidentified,
                        Technical = LosslessTechnical(t.FilePath),
                        IsDownloading = downloading,
                        DownloadSize = size,
                        DownloadSizeLeft = left,
                        LibraryStatus = t.HasFile ? "Downloaded" : t.Monitored ? "Missing" : "Unmonitored"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load library songs");
        }

        lock (_gate)
        {
            _songsCache = songs;
            _songsCacheAt = DateTime.UtcNow;
        }
        return songs;
    }

    /// <summary>The in-flight download state for one library track: its own grab first (a single-song
    /// grab is bound by Deezer track id), then the album grab covering it (a whole-album torrent).</summary>
    static (bool Downloading, long Size, long Left) SongDownloadState(
        Track track, int albumId,
        Dictionary<long, TrackGrabState> trackGrabs, Dictionary<int, AlbumGrabState> albumGrabs)
    {
        if (track.HasFile)
            return (false, 0, 0);
        if (track.DeezerId > 0 && trackGrabs.TryGetValue(track.DeezerId, out TrackGrabState? trackGrab) && trackGrab.IsDownloading)
            return (true, trackGrab.Size, trackGrab.Remaining);
        if (albumGrabs.TryGetValue(albumId, out AlbumGrabState? albumGrab) && albumGrab.IsDownloading)
            return (true, albumGrab.Size, albumGrab.Remaining);
        return (false, 0, 0);
    }

    /// <summary>Cheap lossless hint from a placed file's extension (drives the gold "Lossless"
    /// text on song cards without probing every file).</summary>
    static MusicTechnical? LosslessTechnical(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        bool lossless = ext is ".flac" or ".alac" or ".wav" or ".ape" or ".wv" or ".tta" or ".tak" or ".dsf" or ".dff";
        return lossless ? new MusicTechnical { IsLossless = true, Codec = ext.TrimStart('.').ToUpperInvariant() } : null;
    }

    public async Task<List<MusicItem>> GetLibraryArtistAlbumsAsync(int artistId, CancellationToken ct = default)
    {
        List<Album> albums = await _library.GetArtistAlbumsAsync(artistId, ct);
        return albums
            .Where(a => a.Monitored || a.TrackFileCount > 0)
            .Select(a => MusicItem.FromAlbum(a) with { ArtUrl = ArtFor(a) })
            .ToList();
    }

    /// <summary>
    /// Drops the membership and song caches so the next read reflects the current library. The
    /// ffprobe technical cache is left alone: it changes only when files are imported, and clearing
    /// it on every popup close re-probes every file.
    /// </summary>
    public void InvalidateLibraryCache()
    {
        lock (_gate)
        {
            _libraryCacheAt = DateTime.MinValue;
            _albumIndexAt = DateTime.MinValue;
            _songsCache = null;
            _libraryViewCache = null;
        }
        _library.InvalidateAvailability();
    }

    long _librarySizeBytes;
    DateTime _librarySizeAt = DateTime.MinValue;

    /// <summary>Total bytes of the music library on disk, measured from the real library root.</summary>
    public async Task<long> GetLibrarySizeBytesAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _librarySizeAt < TimeSpan.FromSeconds(60))
            return _librarySizeBytes;

        long total = 0;
        try
        {
            string root = _acquisition.LibraryRoot;
            if (Directory.Exists(root))
            {
                total = await Task.Run(() =>
                {
                    long sum = 0;
                    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        try { sum += new FileInfo(file).Length; }
                        catch { /* skip unreadable files */ }
                    }
                    return sum;
                }, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not measure the music library size");
        }

        _librarySizeBytes = total;
        _librarySizeAt = DateTime.UtcNow;
        _logger.LogDebug("Music library size: {Bytes} bytes at {Root}", total, _acquisition.LibraryRoot);
        return total;
    }

    // Writes (admin)

    public async Task AddArtistToLibraryAsync(MusicItem artist, int? qualityProfileId = null, CancellationToken ct = default)
    {
        // Deezer is the identity provider: a discover artist already carries its id. An item that
        // doesn't (e.g. re-adding a download-derived artist) is matched by name instead.
        MusicItem resolved = artist.DeezerId > 0
            ? artist
            : await FindArtistByNameAsync(artist.Title, ct) ?? artist;
        Artist added = await _library.AddOrUpdateArtistAsync(resolved, ct);

        // Pull the artist's albums (metadata only, unmonitored) so they show in the library view.
        try
        {
            if (resolved.DeezerId > 0)
            {
                List<MusicItem> albums = await _deezer.GetArtistAlbumsAsync(resolved.DeezerId, 50, ct);
                await _library.AddArtistAlbumsAsync(added.Id, albums, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not import albums for artist {Artist}", added.Name);
        }

        await _library.RecordIntentAsync(LibraryIntentKind.Artist, added.Id, added.Name, added.Name, qualityProfileId is > 0 ? qualityProfileId.Value : 1, ct);

        InvalidateLibraryCache();
        if (_notifications is not null)
            await _notifications.ArtistAddedAsync(added.Name, ct);
    }

    public async Task AddAlbumToLibraryAsync(MusicItem album, int? qualityProfileId = null, CancellationToken ct = default)
    {
        // Deezer is the identity provider: a discover album already carries its id. An item without
        // one (e.g. re-adding a download-derived album) is matched by artist+title instead.
        MusicItem resolved = album.DeezerId > 0
            ? album
            : await FindAlbumByNameAsync(album.ArtistName, album.Title, ct) ?? album;

        // Prefer the authoritative Deezer album detail (it carries the track list) for tagging.
        MusicItem detail = resolved;
        try
        {
            if (resolved.DeezerId > 0)
            {
                MusicItem? full = await _deezer.GetAlbumAsync(resolved.DeezerId, ct);
                if (full is not null)
                    detail = full;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load Deezer detail for {Title}", resolved.Title);
        }

        if (detail.DeezerId <= 0 && detail.Tracks.Count == 0)
            throw new InvalidOperationException($"Could not find \"{album.Title}\" on Deezer, so it can't be added.");

        Album? added = await _library.AddOrUpdateAlbumAsync(detail, ct);
        if (added is null)
            throw new InvalidOperationException("The album could not be added to the library.");

        // Always record the intent: an explicit add (even with no quality profile chosen) must be
        // distinguishable from an album that only exists because one of its songs was grabbed, the
        // implicit-album reconcile keys on this.
        await _library.RecordIntentAsync(LibraryIntentKind.Album, added.Id, added.Title, added.ArtistName, qualityProfileId is > 0 ? qualityProfileId.Value : 1, ct);

        InvalidateLibraryCache();
        if (_notifications is not null)
            await _notifications.AlbumAddedAsync(added, ct);

        await TryAcquireAsync(added.ArtistName, added.Title, added.Id, ct);
    }

    async Task TryAcquireAsync(string artist, string title, int? albumId, CancellationToken ct)
    {
        // Acquisition is queued (drained every minute, or kicked now), never run inline: indexer and
        // Soulseek searches are slow, and a failure must fall behind healthier requests, not retry
        // immediately.
        if (_backgroundJobs is not null)
        {
            // Adding an album is an explicit request: re-arm any parked row so it gets a fresh set of
            // automatic attempts.
            await _library.RearmAcquireAsync(artist, title, albumId ?? 0, deezerTrackId: 0, ct);
            await _library.EnqueueAcquireAsync(artist, title, albumId ?? 0, deezerTrackId: 0, requireArtist: false, ct);
            Jobs.MusicAcquireQueueJob.Kick(_backgroundJobs);
            _logger.LogDebug("Queued acquisition for \"{Title}\"", title);
            return;
        }

        try
        {
            MusicGrabResult grab = await _acquisition.SearchAndGrabAsync(artist, title, albumId, ct);
            if (grab.Success)
                _logger.LogInformation("Acquired \"{Title}\": {Message}", title, grab.Message);
            else
                _logger.LogDebug("No grab for \"{Title}\": {Message}", title, grab.Message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Acquisition failed for {Title}", title);
        }
    }

    // Acquisition passthroughs

    public Task<List<MusicRelease>> GetReleasesAsync(string artist, string title, CancellationToken ct = default) =>
        _acquisition.SearchReleasesAsync(artist, title, ct);

    /// <summary>Streams scored release candidates as each indexer query returns (interactive search).</summary>
    public IAsyncEnumerable<MusicRelease> StreamReleasesAsync(string artist, string title, CancellationToken ct = default) =>
        _acquisition.StreamReleasesAsync(artist, title, requireArtist: false, ct);

    public async Task<MusicGrabResult> GrabReleaseAsync(MusicRelease release, int? albumId = null, CancellationToken ct = default)
    {
        // Picking a release in interactive search is explicit: re-arm a parked album so a failed pick
        // doesn't leave it parked forever.
        if (albumId is > 0)
        {
            Album? album = await _library.GetAlbumAsync(albumId.Value, ct);
            if (album is not null)
                await _library.RearmAcquireAsync(album.ArtistName, album.Title, album.Id, deezerTrackId: 0, ct);
        }
        return await _acquisition.GrabAsync(release, albumId, ct);
    }

    public Task<MusicGrabResult> SearchAndGrabAsync(string artist, string title, CancellationToken ct = default) =>
        _acquisition.SearchAndGrabAsync(artist, title, ct);

    public Task<MusicGrabResult> SearchTrackAndGrabAsync(string artist, string title, CancellationToken ct = default) =>
        _acquisition.SearchTrackAndGrabAsync(artist, title, ct);

    public Task<MusicGrabResult> SearchTrackAndGrabAsync(string artist, string title, long deezerTrackId, CancellationToken ct = default) =>
        _acquisition.SearchTrackAndGrabAsync(artist, title, deezerTrackId, ct);

    public async Task<MusicGrabResult> SearchAndGrabAlbumAsync(int albumId, CancellationToken ct = default)
    {
        Album? album = await _library.GetAlbumAsync(albumId, ct);
        if (album is null)
            return new MusicGrabResult(false, null, "Album not found in the library.");
        // An explicit search re-arms a parked request so an attempt-capped album gets a
        // fresh set of automatic retries if this manual attempt misses too.
        await _library.RearmAcquireAsync(album.ArtistName, album.Title, album.Id, deezerTrackId: 0, ct);
        return await _acquisition.SearchAndGrabAsync(album.ArtistName, album.Title, album.Id, ct);
    }

    public async Task<int> DownloadPlaylistAlbumsAsync(MusicItem playlist, int maxAlbums = 25, CancellationToken ct = default)
    {
        if (playlist.DeezerId <= 0) return 0;

        List<MusicItem> tracks = await _deezer.GetPlaylistTracksAsync(playlist.DeezerId, 100, ct);
        List<(string Artist, string Album, long DeezerId)> albums = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.AlbumName))
            .Select(t => (Artist: t.ArtistName, Album: t.AlbumName, t.AlbumDeezerId))
            .Distinct()
            .Take(maxAlbums)
            .ToList();

        int added = 0;
        foreach ((string artist, string album, long deezerId) in albums)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                MusicItem item = new()
                {
                    EntityType = MusicEntityType.Album,
                    Title = album,
                    ArtistName = artist,
                    DeezerId = deezerId
                };
                await AddAlbumToLibraryAsync(item, null, ct);
                added++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not add playlist album {Album}", album);
            }
        }
        return added;
    }

    public async Task RemoveAlbumAsync(MusicItem album, bool deleteFiles = true, CancellationToken ct = default)
    {
        if (album.LibraryId > 0)
            await _library.DeleteAlbumAsync(album.LibraryId, ct);
        InvalidateLibraryCache();
    }

    public async Task RemoveArtistAsync(MusicItem artist, bool deleteFiles = true, CancellationToken ct = default)
    {
        if (artist.LibraryId > 0)
            await _library.DeleteArtistAsync(artist.LibraryId, ct);
        InvalidateLibraryCache();
    }

    public Task SearchAlbumAsync(int albumId, CancellationToken ct = default) => SearchAndGrabAlbumAsync(albumId, ct);

    public async Task SearchArtistAsync(int artistId, CancellationToken ct = default)
    {
        List<Album> albums = await _library.GetArtistAlbumsAsync(artistId, ct);
        foreach (Album album in albums.Where(a => a.TrackFileCount < a.TotalTrackCount))
        {
            if (ct.IsCancellationRequested) break;
            await _library.RearmAcquireAsync(album.ArtistName, album.Title, album.Id, deezerTrackId: 0, ct);
            await _acquisition.SearchAndGrabAsync(album.ArtistName, album.Title, album.Id, ct);
        }
    }

    public Task<List<(int Id, string Name)>> GetQualityProfilesAsync(CancellationToken ct = default)
    {
        List<MusicQualityProfile> profiles = _musicConfig?.CurrentValue.Profiles is { Count: > 0 } configured
            ? configured
            : MusicConfig.DefaultProfiles();
        return Task.FromResult(profiles.Select((profile, index) => (Id: index + 1, Name: profile.Name)).ToList());
    }

    /// <summary>Audio technical info for an album, probed from its placed files (ffprobe).</summary>
    public Task<MusicTechnical> GetAlbumTechnicalAsync(int albumId, CancellationToken ct = default) =>
        _acquisition.GetAlbumTechnicalAsync(albumId, ct);

    /// <summary>Audio technical info for one placed track (per-track Media Info / Lossless badge).</summary>
    public Task<MusicTechnical> GetTrackTechnicalAsync(int albumId, int trackId, CancellationToken ct = default) =>
        _acquisition.GetTrackTechnicalAsync(albumId, trackId, ct);

    /// <summary>Grabs a single missing track (Soulseek-first) in the background, so the album dialog
    /// can fill one song without searching the whole album.</summary>
    public Task RequestTrackGrabAsync(int albumId, string artist, string title, CancellationToken ct = default) =>
        RequestTrackGrabAsync(albumId, artist, title, deezerTrackId: 0, ct);

    /// <summary>As <see cref="RequestTrackGrabAsync(int,string,string,CancellationToken)"/>, carrying the
    /// track's Deezer id so the finished file binds to that exact song.</summary>
    public async Task RequestTrackGrabAsync(int albumId, string artist, string title, long deezerTrackId, CancellationToken ct = default)
    {
        // A user-requested song re-arms a parked queue row: explicitly searching it is the way out of
        // the attempt cap.
        await _library.RearmAcquireAsync(artist, title, albumId, deezerTrackId, ct);
        if (_backgroundJobs is not null)
        {
            await EnqueueTrackAcquireAsync(artist, title, deezerTrackId, albumId, ct);
            KickAcquireQueue();
            return;
        }
        await _acquisition.SearchTrackAndGrabAsync(artist, title, deezerTrackId, ct);
    }

    /// <summary>Adds a song request to the acquisition queue (call <see cref="KickAcquireQueue"/> to run it now).</summary>
    public async Task EnqueueTrackAcquireAsync(string artist, string title, long deezerTrackId, int albumId = 0, CancellationToken ct = default)
    {
        await _library.EnqueueAcquireAsync(artist, title, albumId, deezerTrackId, requireArtist: true, ct);
        _logger.LogInformation("Queued a track grab for \"{Title}\" by {Artist}", title, artist);
    }

    /// <summary>Runs the acquisition queue now instead of waiting for its next minute.</summary>
    public void KickAcquireQueue() => Jobs.MusicAcquireQueueJob.Kick(_backgroundJobs);

    /// <summary>
    /// Re-searches a song from scratch by its identity: drops any file we already hold for it (so a
    /// wrong import can be replaced, not kept) and queues a fresh grab. Backs the song "Research"
    /// action in the album and playlist popups.
    /// </summary>
    public async Task ResearchTrackAsync(string artist, string title, long deezerTrackId, CancellationToken ct = default)
    {
        (int AlbumId, int TrackId)? found = await _library.FindTrackAsync(deezerTrackId, title, ct);
        if (found is { } track)
            await DeleteTrackFileAsync(track.AlbumId, track.TrackId, ct);
        await RequestTrackGrabAsync(found?.AlbumId ?? 0, artist, title, deezerTrackId, ct);
    }

    /// <summary>Deletes a song's placed file, resolved from its identity (album + track).</summary>
    public async Task<bool> DeleteTrackForSongAsync(string title, long deezerTrackId, CancellationToken ct = default)
    {
        (int AlbumId, int TrackId)? found = await _library.FindTrackAsync(deezerTrackId, title, ct);
        return found is { } track && await DeleteTrackFileAsync(track.AlbumId, track.TrackId, ct);
    }

    /// <summary>
    /// Forces a lyrics lookup for one library track and writes the sidecar. Backs the song menu's
    /// "Find lyrics" action, so a track with no lyrics can be retried without waiting for the sweep.
    /// </summary>
    public async Task<Lyrics.LyricsRequestResult> RequestLyricsAsync(int trackId, CancellationToken ct = default)
    {
        if (_lyrics is null || trackId <= 0)
            return new Lyrics.LyricsRequestResult(Lyrics.LyricsOutcome.NotFound, "Lyrics aren't available.");
        return await _lyrics.RequestAsync(trackId, ct);
    }

    /// <summary>
    /// Forces a lyrics lookup for a song identified by title + Deezer track id (how a playlist row
    /// knows it, since it carries no library track id).
    /// </summary>
    public async Task<Lyrics.LyricsRequestResult> RequestLyricsForSongAsync(string title, long deezerTrackId, CancellationToken ct = default)
    {
        (int AlbumId, int TrackId)? found = await _library.FindTrackAsync(deezerTrackId, title, ct);
        return found is { } track
            ? await RequestLyricsAsync(track.TrackId, ct)
            : new Lyrics.LyricsRequestResult(Lyrics.LyricsOutcome.NotFound, "That song isn't in the library yet.");
    }

    /// <summary>Where a library track's lyrics lookup stands (drives the song menu).</summary>
    public async Task<LyricsState> GetLyricsStateAsync(int trackId, CancellationToken ct = default)
        => _lyrics is null || trackId <= 0 ? LyricsState.None : await _lyrics.GetStateAsync(trackId, ct);

    /// <summary>Media info for a song resolved from its identity (empty when we don't hold it).</summary>
    public async Task<MusicTechnical> GetTrackTechnicalForSongAsync(string title, long deezerTrackId, CancellationToken ct = default)
    {
        (int AlbumId, int TrackId)? found = await _library.FindTrackAsync(deezerTrackId, title, ct);
        return found is { } track ? await _acquisition.GetTrackTechnicalAsync(track.AlbumId, track.TrackId, ct) : new MusicTechnical();
    }

    /// <summary>
    /// Deletes a placed track's file and clears it from the library (so it reads as missing again).
    /// Backs the song's "Delete file" action and the drop step of a force re-search.
    /// </summary>
    public async Task<bool> DeleteTrackFileAsync(int albumId, int trackId, CancellationToken ct = default)
    {
        if (trackId <= 0)
            return false;

        Album? album = await _library.GetAlbumAsync(albumId, ct);
        Track? track = album?.Tracks.FirstOrDefault(t => t.Id == trackId);
        if (track is null)
            return false;

        if (!string.IsNullOrEmpty(track.FilePath))
        {
            try
            {
                string full = Path.Combine(_acquisition.LibraryRoot, track.FilePath);
                if (File.Exists(full))
                    File.Delete(full);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete the file for track {TrackId}", trackId);
            }
        }

        await _library.ClearTrackFileAsync(trackId, ct);
        _acquisition.InvalidateTechnicalCache();
        InvalidateLibraryCache();
        return true;
    }

    /// <summary>
    /// Backfills missing per-track Deezer ids for albums that already have a Deezer id (added before
    /// we persisted track ids). Makes the album popup and the Songs tab agree on what's identified.
    /// </summary>
    public async Task<int> BackfillTrackDeezerIdsAsync(CancellationToken ct = default)
    {
        List<Album> albums;
        try
        {
            albums = await _library.GetAllAlbumsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load the library for the Deezer-id backfill");
            return 0;
        }

        int updated = 0;
        foreach (Album album in albums.Where(a => a.DeezerId > 0 && a.Tracks.Any(t => t.DeezerId <= 0)))
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                MusicItem? detail = await _deezer.GetAlbumAsync(album.DeezerId, ct);
                if (detail is null || detail.Tracks.Count == 0)
                    continue;

                Dictionary<int, long> map = [];
                foreach (Track track in album.Tracks.Where(t => t.DeezerId <= 0))
                {
                    MusicTrackItem? match =
                        detail.Tracks.FirstOrDefault(d => d.DiscNumber == track.MediumNumber && d.Number == track.AbsoluteTrackNumber)
                        ?? detail.Tracks.FirstOrDefault(d => d.DiscNumber == track.MediumNumber && d.Number == track.TrackNumber)
                        ?? detail.Tracks.FirstOrDefault(d => Normalize(d.Title) == Normalize(track.Title));
                    if (match is { DeezerId: > 0 })
                        map[track.Id] = match.DeezerId;
                }
                updated += await _library.SetTrackDeezerIdsAsync(album.Id, map, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not backfill Deezer ids for album {AlbumId}", album.Id);
            }
        }
        return updated;
    }

    /// <summary>Re-tags downloaded files whose path wasn't recorded (older imports) so their tags
    /// match the library metadata Navidrome reads. Capped per run.</summary>
    public async Task<int> BackfillTrackFilePathsAsync(CancellationToken ct = default)
    {
        List<Album> albums;
        try
        {
            albums = await _library.GetAllAlbumsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load the library for the file-path backfill");
            return 0;
        }

        int tagged = 0;
        int done = 0;
        DateTime cutoff = DateTime.UtcNow.AddHours(-24);
        foreach (Album album in albums.Where(a => a.Tracks.Any(t => t.HasFile && string.IsNullOrEmpty(t.FilePath))
            && (a.RetagAttemptedAt is null || a.RetagAttemptedAt < cutoff)))
        {
            if (ct.IsCancellationRequested || done >= 80)
                break;
            tagged += await _acquisition.RetagAlbumFilesAsync(album.Id, ct);
            await _library.MarkRetagAttemptedAsync(album.Id, ct);
            done++;
        }
        return tagged;
    }

    /// <summary>
    /// Re-identifies albums that couldn't be matched to Deezer (junk folder/title names): it resolves
    /// them from their tracks, upgrades the album in place (real metadata + track list) and re-tags the
    /// placed files. Albums that still can't be identified have their tracks re-acquired (and, per
    /// config, the untrusted files removed). Idempotent and capped per run.
    /// </summary>
    public async Task<MusicRescueResult> RescueUnidentifiedAsync(CancellationToken ct = default)
    {
        MusicConfig? cfg = _musicConfig?.CurrentValue;
        if (cfg is { RescueUnidentified: false })
            return new MusicRescueResult(0, 0, 0);

        List<Album> albums;
        try
        {
            albums = await _library.GetAllAlbumsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load the library for the unidentified rescue");
            return new MusicRescueResult(0, 0, 0);
        }

        DateTime cutoff = DateTime.UtcNow.AddHours(-24);
        List<Album> unidentified = [.. albums
            .Where(a => a.TrackFileCount > 0
                && a.DeezerId <= 0
                && (a.IdentificationAttemptedAt is null || a.IdentificationAttemptedAt < cutoff))
            .Take(50)];

        int rescued = 0;
        int retagged = 0;
        int requeued = 0;
        foreach (Album album in unidentified)
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                MusicItem? detail = await _acquisition.IdentifyAlbumAsync(
                    album.ArtistName, album.Title, [.. album.Tracks.Select(t => t.Title)], ct);
                if (detail is { DeezerId: > 0 })
                {
                    Album? upgraded = await _library.UpgradeAlbumAsync(album.Id, detail, ct);
                    if (upgraded is not null)
                    {
                        rescued++;
                        // Only re-tag when the identity actually changed (skip no-op re-identifications).
                        if (upgraded.DeezerId != album.DeezerId || Normalize(upgraded.Title) != Normalize(album.Title))
                            retagged += await _acquisition.RetagAlbumFilesAsync(upgraded.Id, ct);
                    }
                    else
                    {
                        requeued += await ReacquireUnidentifiedAsync(album, cfg, ct);
                    }
                }
                else
                {
                    requeued += await ReacquireUnidentifiedAsync(album, cfg, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not rescue album {AlbumId}", album.Id);
            }
            finally
            {
                // Mark the attempt so a failed identification isn't retried (and re-grabbed) every run.
                await _library.MarkIdentificationAttemptedAsync(album.Id, ct);
            }
        }

        if (rescued > 0)
            InvalidateLibraryCache();
        return new MusicRescueResult(rescued, retagged, requeued);
    }

    /// <summary>Queues a fresh grab for each unidentified track, removing the untrusted file first.</summary>
    async Task<int> ReacquireUnidentifiedAsync(Album album, MusicConfig? cfg, CancellationToken ct)
    {
        if (_backgroundJobs is null)
            return 0;

        bool deleteUntrusted = cfg?.DeleteUntrustedTracks ?? true;
        int queued = 0;
        foreach (Track track in album.Tracks.Where(t => t.HasFile && !string.IsNullOrWhiteSpace(t.Title)))
        {
            if (ct.IsCancellationRequested || queued >= 40)
                break;

            string artist = album.ArtistName;
            string title = track.Title;
            await _library.EnqueueAcquireAsync(artist, title, album.Id, track.DeezerId, requireArtist: true, ct);
            queued++;

            if (deleteUntrusted && !string.IsNullOrEmpty(track.FilePath))
            {
                try
                {
                    string full = Path.Combine(_acquisition.LibraryRoot, track.FilePath);
                    if (File.Exists(full))
                        File.Delete(full);
                    await _library.ClearTrackFileAsync(track.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not remove the untrusted file for track {TrackId}", track.Id);
                }
            }
        }

        if (queued > 0)
        {
            Jobs.MusicAcquireQueueJob.Kick(_backgroundJobs);
            _logger.LogInformation("Queued {Count} re-grab(s) for unidentified album \"{Album}\" by {Artist}", queued, album.Title, album.ArtistName);
        }
        return queued;
    }

    public Task ToggleAlbumMonitoredAsync(int albumId, bool monitored, CancellationToken ct = default) =>
        _library.SetAlbumMonitoredAsync(albumId, monitored, ct);

    public Task ToggleArtistMonitoredAsync(int artistId, bool monitored, CancellationToken ct = default) =>
        _library.SetArtistMonitoredAsync(artistId, monitored, ct);

    // Membership

    async Task<List<MusicItem>> AnnotateAsync(List<MusicItem> items, CancellationToken ct)
    {
        (HashSet<long> artistDeezerIds, HashSet<string> names) = await GetLibraryArtistIndexAsync(ct);
        (Dictionary<string, LibraryAlbumRef> albumByKey, Dictionary<long, LibraryAlbumRef> albumByDeezer) = await GetLibraryAlbumIndexAsync(ct);
        (HashSet<long> activeTracks, HashSet<long> activeAlbums) = await _acquisition.GetActiveGrabDeezerIdsAsync(ct);

        // A discovery song has no library row of its own, so its state is inferred from the album we
        // hold for it (matched by Deezer id, else artist+album), enough for the card to agree with
        // the library's songs. An item with an in-flight grab shows as downloading.
        return items.Select(i => AnnotateDownloading(
            i.EntityType switch
            {
                MusicEntityType.Album => AnnotateAlbum(i, albumByKey, albumByDeezer),
                MusicEntityType.Track => AnnotateTrack(i, albumByKey, albumByDeezer, artistDeezerIds, names),
                _ => AnnotateEntity(i, artistDeezerIds, names)
            },
            activeTracks, activeAlbums)).ToList();
    }

    /// <summary>Marks a discovery item as downloading when an active grab matches its Deezer id.</summary>
    static MusicItem AnnotateDownloading(MusicItem item, HashSet<long> activeTracks, HashSet<long> activeAlbums)
    {
        bool downloading = item.EntityType switch
        {
            MusicEntityType.Track => activeTracks.Contains(item.DeezerId)
                || (item.AlbumDeezerId > 0 && activeAlbums.Contains(item.AlbumDeezerId)),
            MusicEntityType.Album => item.DeezerId > 0 && activeAlbums.Contains(item.DeezerId),
            _ => false
        };
        return downloading ? item with { IsDownloading = true, DownloadStatus = "Downloading" } : item;
    }

    static MusicItem AnnotateTrack(
        MusicItem track,
        Dictionary<string, LibraryAlbumRef> albumByKey,
        Dictionary<long, LibraryAlbumRef> albumByDeezer,
        HashSet<long> artistDeezerIds,
        HashSet<string> names)
    {
        LibraryAlbumRef reference = default;
        bool albumFound = track.AlbumDeezerId > 0 && albumByDeezer.TryGetValue(track.AlbumDeezerId, out reference);
        if (!albumFound && !string.IsNullOrWhiteSpace(track.AlbumName))
            albumFound = albumByKey.TryGetValue(AlbumIndexKey(track.ArtistName, track.AlbumName), out reference);

        MusicItem annotated = AnnotateEntity(track, artistDeezerIds, names);
        if (!albumFound)
            return annotated;

        // Only claim the file when the album is fully held; a partial album still gets no per-song
        // badge, which keeps the discovery card honest about what's playable.
        return annotated with
        {
            IsInLibrary = true,
            LibraryId = reference.Id,
            HasFile = reference.HasFile
        };
    }

    async Task<MusicItem> AnnotateArtistAsync(MusicItem artist, CancellationToken ct)
    {
        (HashSet<long> artistDeezerIds, HashSet<string> names) = await GetLibraryArtistIndexAsync(ct);
        return AnnotateEntity(artist, artistDeezerIds, names);
    }

    static MusicItem AnnotateEntity(MusicItem item, HashSet<long> artistDeezerIds, HashSet<string> names)
    {
        bool byId = (item.ArtistDeezerId > 0 && artistDeezerIds.Contains(item.ArtistDeezerId)) ||
                    (item.DeezerId > 0 && artistDeezerIds.Contains(item.DeezerId));
        bool byName = (!string.IsNullOrEmpty(item.ArtistName) && names.Contains(item.ArtistName)) ||
                      (!string.IsNullOrEmpty(item.Title) && names.Contains(item.Title));
        return item with { IsInLibrary = byId || byName };
    }

    /// <summary>
    /// Marks a discovery album as already-in-library (with the file state we hold) when we have it;
    /// matched by Deezer id, or by artist+title, so its card and popup render identically to the
    /// library's. Without the file state a held album would show no "In Library" badge in discovery.
    /// </summary>
    static MusicItem AnnotateAlbum(
        MusicItem album, Dictionary<string, LibraryAlbumRef> byKey, Dictionary<long, LibraryAlbumRef> byDeezer)
    {
        LibraryAlbumRef reference = default;
        bool found = album.DeezerId > 0 && byDeezer.TryGetValue(album.DeezerId, out reference);
        if (!found)
            found = byKey.TryGetValue(AlbumIndexKey(album.ArtistName, album.Title), out reference);
        if (!found)
            return album;

        int trackCount = Math.Max(album.TrackCount, reference.TrackCount);
        string status = reference.HasFile ? "Downloaded"
            : reference.TrackFileCount > 0 ? "Partial"
            : reference.Monitored ? "Missing"
            : "Unmonitored";
        return album with
        {
            IsInLibrary = true,
            LibraryId = reference.Id,
            IsMonitored = reference.Monitored,
            HasFile = reference.HasFile,
            TrackFileCount = reference.TrackFileCount,
            TrackCount = trackCount,
            LibraryStatus = status
        };
    }

    static string AlbumIndexKey(string artist, string title) => $"{Normalize(artist)}|{Normalize(title)}";

    /// <summary>
    /// Our library's albums keyed by Deezer id and by normalized artist+title, carrying just enough
    /// state (id, file counts, monitored) for a discovery card to render like a library card.
    /// </summary>
    async Task<(Dictionary<string, LibraryAlbumRef> ByKey, Dictionary<long, LibraryAlbumRef> ByDeezer)> GetLibraryAlbumIndexAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _albumIndexAt < LibraryCacheTtl)
                return (_libraryAlbumByKey, _libraryAlbumByDeezer);
        }

        Dictionary<string, LibraryAlbumRef> byKey = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<long, LibraryAlbumRef> byDeezer = [];
        try
        {
            foreach (Album a in await _library.GetAllAlbumsAsync(ct))
            {
                LibraryAlbumRef reference = new(a.Id, a.HasFile, a.TrackFileCount, a.TotalTrackCount, a.Monitored);
                byKey.TryAdd(AlbumIndexKey(a.ArtistName, a.Title), reference);
                if (a.DeezerId > 0)
                    byDeezer.TryAdd(a.DeezerId, reference);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not build the music album index");
        }

        lock (_gate)
        {
            _libraryAlbumByKey = byKey;
            _libraryAlbumByDeezer = byDeezer;
            _albumIndexAt = DateTime.UtcNow;
            return (_libraryAlbumByKey, _libraryAlbumByDeezer);
        }
    }

    async Task<(HashSet<long> DeezerIds, HashSet<string> Names)> GetLibraryArtistIndexAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _libraryCacheAt < LibraryCacheTtl)
                return (_libraryArtistDeezerIds, _libraryArtistNames);
        }

        HashSet<long> deezerIds = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (Artist a in await _library.GetAllArtistsAsync(ct))
            {
                if (a.DeezerId > 0) deezerIds.Add(a.DeezerId);
                if (!string.IsNullOrWhiteSpace(a.Name)) names.Add(a.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not build music library index");
        }

        lock (_gate)
        {
            _libraryArtistDeezerIds = deezerIds;
            _libraryArtistNames = names;
            _libraryCacheAt = DateTime.UtcNow;
            return (_libraryArtistDeezerIds, _libraryArtistNames);
        }
    }
}

/// <summary>
/// Minimal library state for a discovery item: the row id plus the file/monitor flags a card needs.
/// Kept small so the membership index is cheap to build and hold.
/// </summary>
public readonly record struct LibraryAlbumRef(int Id, bool HasFile, int TrackFileCount, int TrackCount, bool Monitored);

public record MusicGenre(string Id, string Name);

public record MusicCountry(string Code, string Name, string DeezerName = "");

public record MusicSearchResult(
    MusicItem? TopResult,
    List<MusicItem> Artists,
    List<MusicItem> Albums,
    List<MusicItem> Tracks,
    List<MusicItem> Playlists)
{
    public static MusicSearchResult Empty { get; } = new(null, [], [], [], []);

    public int Total => Artists.Count + Albums.Count + Tracks.Count + Playlists.Count;

    public bool IsEmpty => Total == 0;
}

/// <summary>Outcome of a rescue pass over unidentified albums.</summary>
public record MusicRescueResult(int Rescued, int Retagged, int Requeued)
{
    public bool Changed => Rescued > 0 || Retagged > 0 || Requeued > 0;
}

/// <summary>Aggregated audio technical info for an album/track, probed with ffprobe.</summary>
public record MusicTechnical
{
    public string Codec { get; init; } = "";
    public string Profile { get; init; } = "";
    public string Container { get; init; } = "";
    public string Quality { get; init; } = "";
    public string BitRate { get; init; } = "";
    public string SampleRate { get; init; } = "";
    public string Bits { get; init; } = "";
    public int Channels { get; init; }
    public string ChannelLayout { get; init; } = "";
    public bool IsLossless { get; init; }
    public string Spatial { get; init; } = "";
    public double DurationSeconds { get; init; }
    public long FileSizeBytes { get; init; }

    // Embedded tags (what the importer wrote, i.e. the library metadata).
    public string TagTitle { get; init; } = "";
    public string TagArtist { get; init; } = "";
    public string TagAlbumArtist { get; init; } = "";
    public string TagAlbum { get; init; } = "";
    public string TagTrack { get; init; } = "";
    public string TagDisc { get; init; } = "";
    public string TagDate { get; init; } = "";
    public string TagGenre { get; init; } = "";

    /// <summary>True when any embedded tag was read (so the dialog can show a Tags section).</summary>
    public bool HasTags => !string.IsNullOrEmpty(TagTitle) || !string.IsNullOrEmpty(TagArtist)
        || !string.IsNullOrEmpty(TagAlbum) || !string.IsNullOrEmpty(TagGenre) || !string.IsNullOrEmpty(TagDate);

    public bool HasInfo => !string.IsNullOrEmpty(Codec) || !string.IsNullOrEmpty(Quality);

    public string Badge => !string.IsNullOrEmpty(Spatial) ? Spatial : IsLossless ? "Lossless" : "";

    /// <summary>A friendly channel description ("Mono", "Stereo", "5.1", "7.1").</summary>
    public string ChannelsLabel => !string.IsNullOrEmpty(ChannelLayout)
        ? ChannelLayout
        : Channels switch
        {
            1 => "Mono",
            2 => "Stereo",
            6 => "5.1",
            8 => "7.1",
            > 0 => $"{Channels}ch",
            _ => ""
        };

    /// <summary>The file's running time as m:ss / h:mm:ss (empty when unknown).</summary>
    public string DurationLabel => DurationSeconds <= 0
        ? ""
        : TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    public string FormatSummary
    {
        get
        {
            List<string> parts = [];
            if (!string.IsNullOrEmpty(Spatial)) parts.Add(Spatial);
            if (IsLossless && string.IsNullOrEmpty(Spatial)) parts.Add("Lossless");
            if (!string.IsNullOrEmpty(Codec)) parts.Add(Codec);
            if (!string.IsNullOrEmpty(Bits)) parts.Add(Bits);
            if (!string.IsNullOrEmpty(SampleRate)) parts.Add(SampleRate);
            if (!string.IsNullOrEmpty(BitRate)) parts.Add(BitRate);
            if (Channels > 0) parts.Add($"{Channels}ch");
            return string.Join(" · ", parts.Distinct());
        }
    }
}
