using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Core.Enums;
using Stackarr.Core.Models;
using Stackarr.Core.Playback;
using Stackarr.Mal.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Mal.Services;

/// <summary>
/// Exposes one MAL list status (Plan to Watch or Watching) as a virtual Stackarr watchlist.
/// Nothing is copied into the watchlist tables: the MAL list cache is the source of truth, so
/// changes made on MAL appear automatically and edits in Stackarr are PATCHed back to MAL.
/// </summary>
public sealed class MalWatchlistProvider : IExternalWatchlist, IDisposable
{
    readonly string _status;
    readonly string _name;
    readonly IDbContextFactory<StackarrDbContext> _dbFactory;
    readonly MalAccountService _accounts;
    readonly MalListService _lists;
    readonly MalMappingService _mappings;
    readonly MalMappingDataset _dataset;
    readonly ITvCatalog _library;
    readonly ILogger<MalWatchlistProvider> _logger;

    public MalWatchlistProvider(
        IDbContextFactory<StackarrDbContext> dbFactory,
        MalAccountService accounts,
        MalListService lists,
        MalMappingService mappings,
        MalMappingDataset dataset,
        ITvCatalog library,
        string status,
        string name,
        ILogger<MalWatchlistProvider> logger)
    {
        _status = status;
        _name = name;
        _dbFactory = dbFactory;
        _accounts = accounts;
        _lists = lists;
        _mappings = mappings;
        _dataset = dataset;
        _library = library;
        _logger = logger;
        lists.Changed += OnChanged;
    }

    public string Key => _status == MalStatuses.Watching ? "mal-watching" : "mal-plan";
    public string Name => _name;
    public string Icon => MudBlazor.Icons.Material.Filled.PlaylistPlay;
    public string? IconUrl => "/_content/Stackarr.Mal/img/mal-logo.svg";
    public int Order => _status == MalStatuses.Watching ? 11 : 10;
    public string? ViewAllHref => $"/mal?status={_status}";

    public event Action? Changed;

    void OnChanged() => Changed?.Invoke();

    public async Task<bool> IsAvailableAsync(int userId, CancellationToken ct = default) =>
        await _accounts.GetAsync(userId, ct) is { NeedsReauth: false };

    public async Task<IReadOnlyList<ExternalWatchlistItem>> GetItemsAsync(
        int userId,
        MediaDomain domain,
        CancellationToken ct = default)
    {
        await using StackarrDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        List<MalListEntry> entries = await db.Set<MalListEntry>()
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.Status == _status)
            .ToListAsync(ct);
        if (entries.Count == 0)
            return [];

        List<int> ids = entries.Select(x => x.MalId).ToList();
        Dictionary<int, MalCatalogEntry> catalogs = await db.Set<MalCatalogEntry>()
            .AsNoTracking()
            .Where(x => ids.Contains(x.MalId))
            .ToDictionaryAsync(x => x.MalId, ct);

        try
        {
            await _dataset.EnsureLoadedAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load the MAL mapping dataset for watchlist artwork");
        }

        IReadOnlyDictionary<int, TvSeries> byTmdb = new Dictionary<int, TvSeries>();
        IReadOnlyDictionary<int, TvSeries> byTvdb = new Dictionary<int, TvSeries>();
        try
        {
            (byTmdb, byTvdb) = await _library.GetLibrarySeriesAsync(ct: ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the TV library for MAL watchlist badges");
        }

        List<ExternalWatchlistItem> items = [];
        foreach (MalListEntry entry in entries)
        {
            if (!catalogs.TryGetValue(entry.MalId, out MalCatalogEntry? catalogEntry))
                continue;

            bool movie = string.Equals(catalogEntry.MediaType, "movie", StringComparison.OrdinalIgnoreCase);
            if (movie != (domain == TvMediaDomains.Movie))
                continue;

            MalDatasetEntry? mapping = _dataset.ByMalId(entry.MalId);
            int tmdbId = catalogEntry.TmdbId ?? mapping?.TmdbTvId ?? mapping?.TmdbMovieIds.FirstOrDefault() ?? 0;
            int tvdbId = mapping?.TvdbId ?? 0;

            // The watchlist opens Stackarr's own TV/movie popup. Without a TMDB identity there is
            // nothing for that popup to resolve, so hide the entry rather than falling back to MAL.
            if (tmdbId <= 0)
                continue;

            bool inLibrary = false;
            if (movie && tmdbId > 0)
                inLibrary = await _library.GetLibraryMovieAsync(tmdbId, ct) is not null;
            else if (tmdbId > 0 && byTmdb.ContainsKey(tmdbId))
                inLibrary = true;
            else if (tvdbId > 0 && byTvdb.ContainsKey(tvdbId))
                inLibrary = true;

            items.Add(new ExternalWatchlistItem(
                Key: $"{Key}:anime:{entry.MalId}",
                Title: WatchlistTitle(catalogEntry),
                Subtitle: movie ? "Movie" : null,
                PosterUrl: MalCatalogService.Poster(catalogEntry),
                Year: MalCatalogService.YearOf(catalogEntry),
                Domain: movie ? TvMediaDomains.Movie : TvMediaDomains.Tv,
                TmdbId: tmdbId,
                TvdbId: tvdbId,
                Href: null,
                IsInLibrary: inLibrary,
                Overview: string.IsNullOrWhiteSpace(catalogEntry.DisplayOverview)
                    ? catalogEntry.Synopsis
                    : catalogEntry.DisplayOverview,
                Genres: catalogEntry.Genres
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                Rating: catalogEntry.Mean,
                RuntimeMinutes: null,
                SeasonLabel: MalCatalogService.SeasonLabel(catalogEntry)));
        }

        return items.OrderBy(x => x.Title).ToList();
    }

    static string WatchlistTitle(MalCatalogEntry entry) => MalCatalogService.CardTitle(entry);

    public async Task<bool> CanHandleAsync(int userId, WatchlistItemRef item, CancellationToken ct = default)
    {
        if (await _accounts.GetAsync(userId, ct) is null)
            return false;
        IReadOnlyList<int> ids = await _mappings.GetMalIdsForLocalAsync(
            item.TmdbId, item.TvdbId, item.Domain == TvMediaDomains.Movie, ct);
        return ids.Count > 0;
    }

    public async Task<bool> ContainsAsync(int userId, WatchlistItemRef item, CancellationToken ct = default)
    {
        if (await _accounts.GetAsync(userId, ct) is null)
            return false;
        IReadOnlyList<int> ids = await _mappings.GetMalIdsForLocalAsync(
            item.TmdbId, item.TvdbId, item.Domain == TvMediaDomains.Movie, ct);
        IReadOnlyList<int> current = await _mappings.GetCurrentAsync(userId, ids, _status, ct);
        return current.Count > 0;
    }

    public async Task AddAsync(int userId, WatchlistItemRef item, CancellationToken ct = default)
    {
        if (await _accounts.GetAsync(userId, ct) is null)
            return;

        // A stale cache could otherwise downgrade an entry the user has already started on MAL.
        await _lists.EnsureFreshAsync(userId, TimeSpan.Zero, ct);

        IReadOnlyList<int> ids = await _mappings.GetMalIdsForLocalAsync(
            item.TmdbId, item.TvdbId, item.Domain == TvMediaDomains.Movie, ct);
        if (ids.Count == 0)
            return;

        await using StackarrDbContext db = await _dbFactory.CreateDbContextAsync(ct);
        Dictionary<int, string> existing = await db.Set<MalListEntry>()
            .AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.MalId))
            .ToDictionaryAsync(x => x.MalId, x => x.Status, ct);

        foreach (int malId in ids)
        {
            if (existing.TryGetValue(malId, out string? current))
            {
                // Plan to Watch can be promoted to Watching; never downgrade or overwrite a
                // completed/dropped/on-hold entry.
                if (_status == MalStatuses.Watching && current == MalStatuses.PlanToWatch)
                {
                    try
                    {
                        await _lists.UpdateAsync(userId, malId, new MalListUpdate(Status: MalStatuses.Watching), ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not promote MAL entry {MalId} to Watching", malId);
                    }
                }
                continue;
            }

            try
            {
                await _lists.UpdateAsync(userId, malId, new MalListUpdate(Status: _status, Score: 0), ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not add MAL entry {MalId} to {List}", malId, _name);
            }
        }
    }

    public async Task RemoveAsync(int userId, WatchlistItemRef item, CancellationToken ct = default)
    {
        await _lists.EnsureFreshAsync(userId, TimeSpan.Zero, ct);

        IReadOnlyList<int> ids = await _mappings.GetMalIdsForLocalAsync(
            item.TmdbId, item.TvdbId, item.Domain == TvMediaDomains.Movie, ct);
        IReadOnlyList<int> current = await _mappings.GetCurrentAsync(userId, ids, _status, ct);
        foreach (int malId in current)
        {
            try
            {
                await _lists.RemoveAsync(userId, malId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove MAL entry {MalId} from {List}", malId, _name);
            }
        }
    }

    public void Dispose()
    {
        _lists.Changed -= OnChanged;
    }
}

