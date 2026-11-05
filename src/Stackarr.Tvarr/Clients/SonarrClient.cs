using System.Net.Http.Json;
using System.Text.Json;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Clients;

public class SonarrClient : ArrClient
{
    /// <summary>
    /// Sonarr's series lookup goes through TVDB and can be noticeably slower than Radarr's movie
    /// lookup, so give it a full minute before the request is cancelled. The previous 30s default
    /// surfaced as "The request was canceled due to the configured HttpClient.Timeout of 30
    /// seconds elapsing" on intermittent slow searches.
    /// </summary>
    public static readonly TimeSpan SonarrRequestTimeout = TimeSpan.FromMinutes(1);

    public SonarrClient(string url, string apiKey) : base(url, apiKey, null, SonarrRequestTimeout) { }

    public SonarrClient(string url, string apiKey, HttpMessageHandler? handler)
        : base(url, apiKey, handler, SonarrRequestTimeout) { }

    public virtual async Task<List<SeriesDto>> GetSeries(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest("/api/v3/series");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<SeriesDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<SeriesDto?> GetSeries(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/series/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<SeriesDto>(json, JsonOpts);
    }

    public virtual async Task<SeriesDto> CreateSeries(SeriesCreateRequest series, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/series");
        request.Content = JsonContent.Create(series);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<SeriesDto>(json, JsonOpts)!;
    }

    public virtual async Task<SeriesDto> UpdateSeries(SeriesDto series, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v3/series/{series.Id}");
        request.Content = JsonContent.Create(series);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<SeriesDto>(json, JsonOpts)!;
    }

    public virtual async Task DeleteSeries(int id, bool deleteFiles = false, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v3/series/{id}?deleteFiles={deleteFiles}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<EpisodeDto>> GetEpisodes(int seriesId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/episode?seriesId={seriesId}&includeImages=true");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<EpisodeDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task<EpisodeDto?> GetEpisode(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/episode/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<EpisodeDto>(json, JsonOpts);
    }

    public virtual async Task<EpisodeFileDto?> GetEpisodeFile(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/episodefile/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<EpisodeFileDto>(json, JsonOpts);
    }

    public virtual async Task UpdateEpisodeMonitored(int episodeId, bool monitored, CancellationToken ct = default)
    {
        HttpRequestMessage getRequest = CreateRequest($"/api/v3/episode/{episodeId}");
        HttpResponseMessage getResponse = await Http.SendAsync(getRequest, ct);
        getResponse.EnsureSuccessStatusCode();
        string getJson = await getResponse.Content.ReadAsStringAsync(ct);
        EpisodeDto? episode = JsonSerializer.Deserialize<EpisodeDto>(getJson, JsonOpts);
        if (episode == null)
            return;

        episode.Monitored = monitored;
        HttpRequestMessage request = CreateRequest(HttpMethod.Put, $"/api/v3/episode/{episodeId}");
        request.Content = JsonContent.Create(episode);
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<EpisodeFileDto>> GetEpisodeFiles(int seriesId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/episodefile?seriesId={seriesId}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<EpisodeFileDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task DeleteEpisodeFile(int id, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Delete, $"/api/v3/episodefile/{id}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<HistoryDto>> GetSeriesHistory(int seriesId, CancellationToken ct = default)
    {
        return await GetHistory("seriesId", seriesId, ct);
    }

    public virtual async Task<List<SeriesLookupDto>> SearchSeries(string query, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/series/lookup?term={Uri.EscapeDataString(query)}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<SeriesLookupDto>>(json, JsonOpts) ?? [];
    }

    public virtual async Task CommandSearchSeries(int seriesId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new { name = "SeriesSearch", seriesId });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task CommandSearchSeason(int seriesId, int seasonNumber, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new { name = "SeasonSearch", seriesId, seasonNumber });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task CommandSearchMissingEpisodes(CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new { name = "MissingEpisodeSearch" });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task CommandSearchEpisodes(
        IReadOnlyCollection<int> episodeIds, CancellationToken ct = default)
    {
        if (episodeIds.Count == 0)
            return;

        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new { name = "EpisodeSearch", episodeIds = episodeIds.ToArray() });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual Task CommandSearchEpisode(int episodeId, CancellationToken ct = default)
    {
        return CommandSearchEpisodes([episodeId], ct);
    }

    /// <summary>Asks Sonarr to rescan one series folder (used after a local-import recovery).</summary>
    public virtual async Task CommandRescanSeries(int seriesId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/command");
        request.Content = JsonContent.Create(new { name = "RescanSeries", seriesId });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }


    /// <summary>
    /// Lists release candidates for Sonarr's interactive search: a single episode, a season, or the
    /// whole series. At least one of <paramref name="episodeId"/>/<paramref name="seriesId"/> is needed.
    /// </summary>
    public virtual async Task<List<ReleaseResourceDto>> GetReleases(
        int? episodeId = null,
        int? seriesId = null,
        int? seasonNumber = null,
        CancellationToken ct = default)
    {
        List<string> parts = [];
        if (episodeId is > 0)
            parts.Add($"episodeId={episodeId}");
        if (seriesId is > 0)
            parts.Add($"seriesId={seriesId}");
        if (seasonNumber is not null)
            parts.Add($"seasonNumber={seasonNumber}");

        string query = parts.Count > 0 ? "?" + string.Join("&", parts) : "";
        HttpRequestMessage request = CreateRequest($"/api/v3/release{query}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ReleaseResourceDto>>(json, JsonOpts) ?? [];
    }

    /// <summary>Grabs a specific release, letting Sonarr hand it to the configured download client.</summary>
    public virtual async Task GrabRelease(string guid, int indexerId, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/v3/release");
        request.Content = JsonContent.Create(new { guid, indexerId });
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public virtual async Task<List<SonarrCalendarDto>> GetCalendar(DateTime start, DateTime end, CancellationToken ct = default)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<SonarrCalendarDto>>(json, JsonOpts) ?? [];
    }

    public new async Task<List<SonarrQueueItemDto>> GetQueue(CancellationToken ct = default, int pageSize = 20)
    {
        HttpRequestMessage request = CreateRequest($"/api/v3/queue?page=1&pageSize={pageSize}");
        HttpResponseMessage response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        QueuePagingResource<SonarrQueueItemDto>? paged = JsonSerializer.Deserialize<QueuePagingResource<SonarrQueueItemDto>>(json, JsonOpts);
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

public class SeriesDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string Path { get; set; } = "";
    public int QualityProfileId { get; set; }
    public bool SeasonFolder { get; set; }
    public string Status { get; set; } = "";
    public bool Monitored { get; set; }
    public string Overview { get; set; } = "";
    public string Network { get; set; } = "";
    public int? Runtime { get; set; }
    public RatingsDto? Ratings { get; set; }
    public List<string> Genres { get; set; } = [];
    public DateTime Added { get; set; }
    public List<int> Tags { get; set; } = [];
    public string SeriesType { get; set; } = "standard";
    public List<SeasonDto> Seasons { get; set; } = [];
    public List<ImageDto> Images { get; set; } = [];
    public int Year { get; set; }
    public int TvdbId { get; set; }
    public int TmdbId { get; set; }
    public string ImdbId { get; set; } = "";
    public DateTime? FirstAired { get; set; }
    public DateTime? LastAired { get; set; }
    public string Certification { get; set; } = "";
    public LanguageDto? OriginalLanguage { get; set; }
    public string AirTime { get; set; } = "";
    public SeriesStatisticsDto? Statistics { get; set; }
}

public class SeriesStatisticsDto
{
    public int EpisodeFileCount { get; set; }
    public int EpisodeCount { get; set; }
    public int TotalEpisodeCount { get; set; }
    public long SizeOnDisk { get; set; }
}

public class SeasonDto
{
    public int SeasonNumber { get; set; }
    public bool Monitored { get; set; }
    public SeasonStatisticsDto? Statistics { get; set; }
}

public class SeasonStatisticsDto
{
    public int EpisodeCount { get; set; }
    public int EpisodeFileCount { get; set; }
    public int TotalEpisodeCount { get; set; }
    public long SizeOnDisk { get; set; }
}

public class EpisodeDto
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string Title { get; set; } = "";
    public string Overview { get; set; } = "";
    public bool Monitored { get; set; }
    public bool HasFile { get; set; }
    public int EpisodeFileId { get; set; }
    public string? AirDate { get; set; }
    public DateTime? AirDateUtc { get; set; }
    public List<ImageDto> Images { get; set; } = [];
}

public class EpisodeFileDto
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int SeasonNumber { get; set; }
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime DateAdded { get; set; }
    public MediaInfoDto? MediaInfo { get; set; }
    public FileQualityDto? Quality { get; set; }
    public List<int> EpisodeIds { get; set; } = [];
}

public class SeriesLookupDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public int? Year { get; set; }
    public string Overview { get; set; } = "";
    public RatingsDto? Ratings { get; set; }
    public List<string> Genres { get; set; } = [];
    public int TvdbId { get; set; }
    public int TmdbId { get; set; }
    public string ImdbId { get; set; } = "";
    public List<ImageDto> Images { get; set; } = [];
    public int? Runtime { get; set; }
    public string Status { get; set; } = "";
    public string Network { get; set; } = "";
    public string Certification { get; set; } = "";
    public string SeriesType { get; set; } = "standard";
    public DateTime? FirstAired { get; set; }
    public List<SeasonDto> Seasons { get; set; } = [];
    public LanguageDto? OriginalLanguage { get; set; }
}

public record SeriesCreateRequest
{
    public int TvdbId { get; init; }
    public string Title { get; init; } = "";
    public int QualityProfileId { get; init; }
    public string Path { get; init; } = "";
    public bool Monitored { get; init; }
    public bool SeasonFolder { get; init; }
}

public class SonarrCalendarDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public DateTime? AirDate { get; set; }
    public bool HasFile { get; set; }
}

public class SonarrQueueItemDto
{
    public int Id { get; set; }
    public int SeriesId { get; set; }
    public int EpisodeId { get; set; }
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
