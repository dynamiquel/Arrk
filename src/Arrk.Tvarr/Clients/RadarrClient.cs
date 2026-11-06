using System.Net.Http.Json;
using System.Text.Json;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Clients;

public class RadarrClient : ArrClient
{
    public RadarrClient(string url, string apiKey) : base(url, apiKey) { }

    public RadarrClient(string url, string apiKey, HttpMessageHandler? handler)
        : base(url, apiKey, handler) { }

    public virtual async Task<List<MovieDto>> GetMovies(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/movie");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<MovieDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<MovieDto?> GetMovie(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/movie/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<MovieDto>(json, JsonOpts);
    }

    public virtual async Task<MovieDto> CreateMovie(MovieCreateRequest movie, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/movie");
        request.Content = JsonContent.Create(movie);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<MovieDto>(json, JsonOpts)!;
    }

    public virtual async Task<MovieDto> UpdateMovie(MovieDto movie, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v3/movie/{movie.Id}");
        request.Content = JsonContent.Create(movie);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<MovieDto>(json, JsonOpts)!;
    }

    public virtual async Task DeleteMovie(int id, bool deleteFiles = false, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v3/movie/{id}?deleteFiles={deleteFiles}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<MovieFileDto>> GetMovieFiles(int movieId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/moviefile?movieId={movieId}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<MovieFileDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task DeleteMovieFile(int movieFileId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v3/moviefile/{movieFileId}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<HistoryDto>> GetMovieHistory(int movieId, CancellationToken ct = default)
    {
        return await GetHistory("movieId", movieId, ct);
    }

    public virtual async Task<List<MovieLookupDto>> SearchMovie(string query, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/movie/lookup?term={Uri.EscapeDataString(query)}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<MovieLookupDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task CommandSearchMovies(
        IReadOnlyCollection<int> movieIds, CancellationToken ct = default)
    {
        if (movieIds.Count == 0)
            return;

        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new { name = "MoviesSearch", movieIds = movieIds.ToArray() });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual Task CommandSearchMovie(int movieId, CancellationToken ct = default)
    {
        return CommandSearchMovies([movieId], ct);
    }

    /// <summary>Asks Radarr to rescan one movie folder (used after a local-import recovery).</summary>
    public virtual async Task CommandRescanMovie(int movieId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new { name = "RescanMovie", movieId });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Runs Radarr's native search for every monitored movie without a file.</summary>
    public virtual async Task CommandSearchMissingMovies(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new { name = "MissingMoviesSearch" });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }


    /// <summary>Lists release candidates for a movie's interactive search (what Radarr's own UI shows).</summary>
    public virtual async Task<List<ReleaseResourceDto>> GetReleases(int movieId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/release?movieId={movieId}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ReleaseResourceDto>>(json, JsonOpts) ?? [];
    }

    /// <summary>Grabs a specific release, letting Radarr hand it to the configured download client.</summary>
    public virtual async Task GrabRelease(string guid, int indexerId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/release");
        request.Content = JsonContent.Create(new { guid, indexerId });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<RadarrCalendarDto>> GetCalendar(DateTime start, DateTime end, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<RadarrCalendarDto>>(json, JsonOpts) ?? [];
    }

    public new async Task<List<RadarrQueueItemDto>> GetQueue(CancellationToken ct = default, int pageSize = 20)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/queue?page=1&pageSize={pageSize}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        QueuePagingResource<RadarrQueueItemDto>? paged = JsonSerializer.Deserialize<QueuePagingResource<RadarrQueueItemDto>>(json, JsonOpts);
        return paged?.Records ?? [];
    }

    public virtual async Task DeleteQueueItem(
        int queueItemId,
        bool blocklist = false,
        bool removeFromClient = true,
        bool searchAgain = false,
        CancellationToken ct = default)
    {
        await DeleteQueueItemCore(queueItemId, blocklist, removeFromClient, searchAgain, ct);
    }
}

public class MovieDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string Path { get; set; } = "";
    public int QualityProfileId { get; set; }
    public bool HasFile { get; set; }
    public bool Monitored { get; set; }
    public string Status { get; set; } = "";
    public string Overview { get; set; } = "";
    public RatingsDto? Ratings { get; set; }
    public List<string> Genres { get; set; } = [];
    public DateTime Added { get; set; }
    public string MinimumAvailability { get; set; } = "";
    public List<ImageDto> Images { get; set; } = [];
    public List<MovieFileDto> MovieFiles { get; set; } = [];

    /// <summary>
    /// Radarr's movie resource embeds the file as a single <c>movieFile</c> object, not the
    /// <c>movieFiles</c> array. Expose whichever the server sent so callers get the quality and
    /// media info regardless of Radarr version.
    /// </summary>
    public MovieFileDto? MovieFile { get; set; }
    public string ImdbId { get; set; } = "";
    public int Year { get; set; }
    public int TmdbId { get; set; }
    public int? Runtime { get; set; }
    public string Certification { get; set; } = "";
    public LanguageDto? OriginalLanguage { get; set; }
    public DateTime? InCinemas { get; set; }
    public DateTime? PhysicalRelease { get; set; }
    public DateTime? DigitalRelease { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string Studio { get; set; } = "";
    public string YouTubeTrailerId { get; set; } = "";
    public string Website { get; set; } = "";
    public CollectionDto? Collection { get; set; }
    public long SizeOnDisk { get; set; }
}

public class CollectionDto
{
    public string Title { get; set; } = "";
    public int TmdbId { get; set; }
}

public class MovieFileDto
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime DateAdded { get; set; }
    public MediaInfoDto? MediaInfo { get; set; }
    public FileQualityDto? Quality { get; set; }
}

public class MovieLookupDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public int? Year { get; set; }
    public string Overview { get; set; } = "";
    public RatingsDto? Ratings { get; set; }
    public List<string> Genres { get; set; } = [];
    public string ImdbId { get; set; } = "";
    public int TmdbId { get; set; }
    public List<ImageDto> Images { get; set; } = [];
    public int? Runtime { get; set; }
    public string Status { get; set; } = "";
    public string Certification { get; set; } = "";
    public string Studio { get; set; } = "";
    public LanguageDto? OriginalLanguage { get; set; }
    public DateTime? InCinemas { get; set; }
    public DateTime? PhysicalRelease { get; set; }
    public DateTime? DigitalRelease { get; set; }
    public DateTime? ReleaseDate { get; set; }
    public string YouTubeTrailerId { get; set; } = "";
    public CollectionDto? Collection { get; set; }
}

public record MovieCreateRequest
{
    public int TmdbId { get; init; }
    public string Title { get; init; } = "";
    public int QualityProfileId { get; init; }
    public string Path { get; init; } = "";
    public bool Monitored { get; init; }
    public string MinimumAvailability { get; init; } = "";
}

public class RadarrCalendarDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime? PhysicalRelease { get; set; }
    public DateTime? DigitalRelease { get; set; }
    public bool HasFile { get; set; }
}

public class RadarrQueueItemDto
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public string Status { get; set; } = "";
    public string TrackedDownloadStatus { get; set; } = "";
    public string TrackedDownloadState { get; set; } = "";
    public string Title { get; set; } = "";
    public long Size { get; set; }
    public long Sizeleft { get; set; }
    public DateTime? EstimatedCompletionTime { get; set; }
    public DateTime Added { get; set; }
    public string DownloadId { get; set; } = "";
}
