using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Configuration;
using Arrk.Core.Models;

namespace Arrk.Mal.Services;

/// <summary>One row in Fribb's anime-lists dataset, normalised to what the sync needs.</summary>
public sealed record MalDatasetEntry(
    int MalId,
    string Type,
    int? TvdbId,
    int? TmdbTvId,
    IReadOnlyList<int> TmdbMovieIds,
    int? TvdbSeason,
    int? TmdbSeason,
    int? TvdbEpisodeOffset,
    int? TmdbEpisodeOffset)
{
    public bool IsMovie => string.Equals(Type, "MOVIE", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Downloads and indexes the anime-lists mapping dataset. The dataset is the only reliable way to
/// translate a local TV season into MAL's per-season entries, including split cours.
/// </summary>
public sealed class MalMappingDataset(
    IOptionsMonitor<MalConfig> config,
    ILogger<MalMappingDataset> logger)
{
    readonly SemaphoreSlim _loadGate = new(1, 1);
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };

    Dictionary<int, MalDatasetEntry> _byMalId = [];
    Dictionary<(int TvdbId, int Season), List<MalDatasetEntry>> _byTvdbSeason = [];
    Dictionary<(int TmdbId, int Season), List<MalDatasetEntry>> _byTmdbSeason = [];
    Dictionary<int, List<MalDatasetEntry>> _seriesByTvdb = [];
    Dictionary<int, List<MalDatasetEntry>> _seriesByTmdb = [];
    Dictionary<int, List<MalDatasetEntry>> _moviesByTmdb = [];
    DateTime _loadedAt = DateTime.MinValue;
    bool _loaded;

    string FilePath => Path.Combine(ArrkFiles.ConfigDirectory(DataRoute.Root), "mal", "anime-lists.json");

    public DateTime LoadedAt => _loadedAt;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (!config.CurrentValue.Enabled)
            return;

        if (_loaded && DateTime.UtcNow - _loadedAt < TimeSpan.FromHours(12))
            return;

        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded && DateTime.UtcNow - _loadedAt < TimeSpan.FromHours(12))
                return;

            MalConfig current = config.CurrentValue;
            if (!current.Enabled)
                return;

            string path = FilePath;
            bool needsDownload = !File.Exists(path)
                || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromDays(Math.Max(1, current.DatasetRefreshDays));

            if (needsDownload)
            {
                try
                {
                    logger.LogInformation("Refreshing MyAnimeList mapping dataset from {Url}", current.DatasetUrl);
                    string json = await _http.GetStringAsync(current.DatasetUrl, ct);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, json, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not refresh the MyAnimeList mapping dataset; using the cached copy if present");
                    if (!File.Exists(path))
                        throw;
                }
            }

            string content = await File.ReadAllTextAsync(path, ct);
            Index(Parse(content));
            _loadedAt = DateTime.UtcNow;
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public MalDatasetEntry? ByMalId(int malId) =>
        _byMalId.TryGetValue(malId, out MalDatasetEntry? entry) ? entry : null;

    public IReadOnlyList<MalDatasetEntry> TvSeason(int tvdbId, int season) =>
        _byTvdbSeason.TryGetValue((tvdbId, season), out List<MalDatasetEntry>? entries) ? entries : [];

    public IReadOnlyList<MalDatasetEntry> TmdbSeason(int tmdbId, int season) =>
        _byTmdbSeason.TryGetValue((tmdbId, season), out List<MalDatasetEntry>? entries) ? entries : [];

    public IReadOnlyList<MalDatasetEntry> SeriesWideForTvdb(int tvdbId) =>
        _seriesByTvdb.TryGetValue(tvdbId, out List<MalDatasetEntry>? entries)
            ? entries.Where(x => x.TvdbSeason is null).ToList()
            : [];

    public IReadOnlyList<MalDatasetEntry> SeriesWideForTmdb(int tmdbId) =>
        _seriesByTmdb.TryGetValue(tmdbId, out List<MalDatasetEntry>? entries)
            ? entries.Where(x => x.TmdbSeason is null).ToList()
            : [];

    public IReadOnlyList<MalDatasetEntry> AllForTvdb(int tvdbId) =>
        _seriesByTvdb.TryGetValue(tvdbId, out List<MalDatasetEntry>? entries) ? entries : [];

    public IReadOnlyList<MalDatasetEntry> AllForTmdb(int tmdbId) =>
        _seriesByTmdb.TryGetValue(tmdbId, out List<MalDatasetEntry>? entries) ? entries : [];

    public IReadOnlyList<MalDatasetEntry> MoviesForTmdb(int tmdbId) =>
        _moviesByTmdb.TryGetValue(tmdbId, out List<MalDatasetEntry>? entries) ? entries : [];

    static List<MalDatasetEntry> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        List<MalDatasetEntry> entries = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("mal_id", out JsonElement malElement)
                || !malElement.TryGetInt32(out int malId)
                || malId <= 0)
            {
                continue;
            }

            string type = item.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString() ?? ""
                : "";

            int? tvdbId = ReadInt(item, "tvdb_id");
            int? tmdbTvId = null;
            List<int> tmdbMovies = [];
            if (item.TryGetProperty("themoviedb_id", out JsonElement tmdb) && tmdb.ValueKind == JsonValueKind.Object)
            {
                tmdbTvId = ReadInt(tmdb, "tv");
                if (tmdb.TryGetProperty("movie", out JsonElement movies) && movies.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement movie in movies.EnumerateArray())
                    {
                        if (movie.TryGetInt32(out int id))
                            tmdbMovies.Add(id);
                    }
                }
            }

            int? tvdbSeason = null;
            int? tmdbSeason = null;
            if (item.TryGetProperty("season", out JsonElement season) && season.ValueKind == JsonValueKind.Object)
            {
                tvdbSeason = ReadInt(season, "tvdb");
                tmdbSeason = ReadInt(season, "tmdb");
            }

            int? tvdbOffset = null;
            int? tmdbOffset = null;
            if (item.TryGetProperty("episode_offset", out JsonElement offset) && offset.ValueKind == JsonValueKind.Object)
            {
                tvdbOffset = ReadInt(offset, "tvdb");
                tmdbOffset = ReadInt(offset, "tmdb");
            }

            entries.Add(new MalDatasetEntry(
                malId, type, tvdbId, tmdbTvId, tmdbMovies,
                tvdbSeason, tmdbSeason, tvdbOffset, tmdbOffset));
        }

        return entries;
    }

    static int? ReadInt(JsonElement element, string property)
        => element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
            ? number
            : null;

    void Index(List<MalDatasetEntry> entries)
    {
        Dictionary<int, MalDatasetEntry> byMalId = [];
        Dictionary<(int, int), List<MalDatasetEntry>> byTvdbSeason = [];
        Dictionary<(int, int), List<MalDatasetEntry>> byTmdbSeason = [];
        Dictionary<int, List<MalDatasetEntry>> seriesByTvdb = [];
        Dictionary<int, List<MalDatasetEntry>> seriesByTmdb = [];
        Dictionary<int, List<MalDatasetEntry>> moviesByTmdb = [];

        foreach (MalDatasetEntry entry in entries)
        {
            byMalId.TryAdd(entry.MalId, entry);

            if (entry.TvdbId is int tvdb)
            {
                Add(seriesByTvdb, tvdb, entry);
                if (entry.TvdbSeason is int season)
                    Add(byTvdbSeason, (tvdb, season), entry);
            }

            if (entry.TmdbTvId is int tmdbTv)
            {
                Add(seriesByTmdb, tmdbTv, entry);
                if (entry.TmdbSeason is int season)
                    Add(byTmdbSeason, (tmdbTv, season), entry);
            }

            foreach (int movieId in entry.TmdbMovieIds)
                Add(moviesByTmdb, movieId, entry);
        }

        _byMalId = byMalId;
        _byTvdbSeason = byTvdbSeason;
        _byTmdbSeason = byTmdbSeason;
        _seriesByTvdb = seriesByTvdb;
        _seriesByTmdb = seriesByTmdb;
        _moviesByTmdb = moviesByTmdb;
    }

    static void Add<TKey>(Dictionary<TKey, List<MalDatasetEntry>> map, TKey key, MalDatasetEntry entry)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out List<MalDatasetEntry>? list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(entry);
    }
}
