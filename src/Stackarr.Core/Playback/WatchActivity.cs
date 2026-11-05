using Microsoft.EntityFrameworkCore;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Core.Playback;

/// <summary>Stable keys shared by every module that records or consumes playback activity.</summary>
public static class MediaItemKeys
{
    public static string Movie(int tmdbId) => $"tv:movie:tmdb:{tmdbId}";

    public static string Movie(string? tmdbId) =>
        int.TryParse(tmdbId, out int id) && id > 0 ? Movie(id) : "";

    public static string Series(int tvdbId, int tmdbId) =>
        tvdbId > 0 ? $"tv:series:tvdb:{tvdbId}" : tmdbId > 0 ? $"tv:series:tmdb:{tmdbId}" : "";

    public static string Episode(string seriesKey, int season, int episode) =>
        string.IsNullOrEmpty(seriesKey) ? "" : $"{seriesKey}:s{season}e{episode}";

    /// <summary>Container key for one season; episode plays are recorded against it so a season
    /// is the staleness unit for TV.</summary>
    public static string Season(string seriesKey, int season) =>
        string.IsNullOrEmpty(seriesKey) || season <= 0 ? "" : $"{seriesKey}:s{season}";

    /// <summary>
    /// Parses the built-in TV hierarchy from an item key. Used when a module only has the stable
    /// key (for example a watchlist bookmark) and needs to know whether it is a series, season or
    /// episode and which keys must also match.
    /// </summary>
    public static bool TryGetSeriesHierarchy(string itemKey, out MediaKeyHierarchy hierarchy)
    {
        hierarchy = default!;
        const string prefix = "tv:series:";
        if (string.IsNullOrWhiteSpace(itemKey) ||
            !itemKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string rest = itemKey[prefix.Length..];
        int seasonMarker = rest.IndexOf(":s", StringComparison.OrdinalIgnoreCase);
        if (seasonMarker < 0)
        {
            hierarchy = new MediaKeyHierarchy(itemKey, null, IncludesChildren: true);
            return true;
        }

        string seriesKey = itemKey[..(prefix.Length + seasonMarker)];
        string seasonPart = rest[seasonMarker..];
        int episodeMarker = seasonPart.IndexOf('e', 2);
        hierarchy = episodeMarker > 0
            ? new MediaKeyHierarchy(seriesKey, seriesKey + seasonPart[..episodeMarker], IncludesChildren: false)
            : new MediaKeyHierarchy(seriesKey, seriesKey + seasonPart, IncludesChildren: true);
        return true;
    }

    public static string MusicAlbum(int albumId) => $"music:album:{albumId}";

    public static string MusicAlbum(string? artist, string? album) =>
        string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album)
            ? ""
            : $"music:album:{Normalize(artist)}|{Normalize(album)}";

    public static string MusicTrack(string? artist, string? title) =>
        string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title)
            ? ""
            : $"music:track:{Normalize(artist)}|{Normalize(title)}";

    /// <summary>Lowercase letters/digits only, so a song survives tag punctuation changes.</summary>
    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>The series/season/episode shape encoded in a built-in TV item key.</summary>
/// <param name="SeriesKey">The series container key.</param>
/// <param name="SeasonKey">The season key for a season or episode; null for a series.</param>
/// <param name="IncludesChildren">True for a series or season; false for one episode.</param>
public sealed record MediaKeyHierarchy(string SeriesKey, string? SeasonKey, bool IncludesChildren);

/// <summary>Logical media service an activity belongs to.</summary>
public static class WatchActivityServices
{
    public const string Tv = "tv";
    public const string Music = "music";
}

/// <summary>Applications that can report playback.</summary>
public static class WatchActivitySources
{
    public const string Stackarr = "Stackarr";
    public const string Jellyfin = "Jellyfin";
    public const string Navidrome = "Navidrome";
}

/// <summary>Turns a browser User-Agent into a friendly device/client pair for history rows.</summary>
public static class PlaybackDevice
{
    public static (string? Device, string? Client) FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return (null, null);

        string ua = userAgent;
        string? device =
            Contains(ua, "iPhone") ? "iPhone"
            : Contains(ua, "iPad") ? "iPad"
            : Contains(ua, "Android") ? "Android"
            : Contains(ua, "Windows") ? "Windows"
            : Contains(ua, "Macintosh") || Contains(ua, "Mac OS") ? "Mac"
            : Contains(ua, "Linux") ? "Linux"
            : Contains(ua, "AppleTV") || Contains(ua, "Apple TV") ? "Apple TV"
            : null;

        string? client =
            Contains(ua, "Jellyfin") ? "Jellyfin"
            : Contains(ua, "Symfonium") ? "Symfonium"
            : Contains(ua, "Feishin") ? "Feishin"
            : Contains(ua, "Sonixd") ? "Sonixd"
            : Contains(ua, "AirPlay") ? "AirPlay"
            : Contains(ua, "Sonos") ? "Sonos"
            : Contains(ua, "Edg") ? "Edge"
            : Contains(ua, "Firefox") ? "Firefox"
            : Contains(ua, "Chrome") || Contains(ua, "Chromium") ? "Chrome"
            : Contains(ua, "Safari") ? "Safari"
            : null;

        return (device, client);
    }

    static bool Contains(string value, string part) =>
        value.Contains(part, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One playback event (a listen or a watch). Entries are the durable history; a compact
/// <see cref="MediaConsumptionStamp"/> is maintained alongside so staleness checks never have to
/// scan the whole history.
/// </summary>
public class WatchActivityEntry
{
    public long Id { get; set; }

    public int? UserId { get; set; }

    public string Username { get; set; } = "";

    /// <summary>Media service: <see cref="WatchActivityServices"/>.</summary>
    public string ProviderKey { get; set; } = "";

    public MediaDomain Domain { get; set; }

    /// <summary>movie | series | episode | album | track.</summary>
    public string ItemType { get; set; } = "";

    /// <summary>Stable identity, built with <see cref="MediaItemKeys"/>.</summary>
    public string ItemKey { get; set; } = "";

    /// <summary>Container identity (series for an episode, album for a track).</summary>
    public string? ParentItemKey { get; set; }

    public string Title { get; set; } = "";

    public string? Subtitle { get; set; }

    public string? ImageUrl { get; set; }

    /// <summary>Which application reported the play: <see cref="WatchActivitySources"/>.</summary>
    public string Source { get; set; } = "";

    /// <summary>Friendly device name supplied by the player (e.g. "Living room TV").</summary>
    public string? Device { get; set; }

    /// <summary>Player/client name (e.g. "Jellyfin Web", "Symfonium", "Chrome").</summary>
    public string? Client { get; set; }

    public string? IpAddress { get; set; }

    /// <summary>Player session identity, used to update one play instead of inserting duplicates.</summary>
    public string? SessionKey { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? LastSeenAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public long PositionTicks { get; set; }

    public long RuntimeTicks { get; set; }

    public double Progress { get; set; }

    public bool Completed { get; set; }

    public int TmdbId { get; set; }

    public int TvdbId { get; set; }

    public int SeasonNumber { get; set; }

    public int EpisodeNumber { get; set; }

    public long? LibraryId { get; set; }

    public string ScopeLabel => ItemType switch
    {
        "episode" when SeasonNumber > 0 => $"S{SeasonNumber}E{EpisodeNumber}",
        _ => ""
    };
}

/// <summary>Latest consumption of one media item, used by staleness checks and UI badges.</summary>
public class MediaConsumptionStamp
{
    public long Id { get; set; }

    public string ProviderKey { get; set; } = "";

    public string ItemKey { get; set; } = "";

    public string ItemType { get; set; } = "";

    public string? ParentItemKey { get; set; }

    public string? Title { get; set; }

    public string? ImageUrl { get; set; }

    public DateTime LastConsumedAt { get; set; }

    public string? LastUsername { get; set; }

    public string? LastSource { get; set; }

    public string? LastDevice { get; set; }
}

/// <summary>Input for recording one play. Values are normalised by <see cref="WatchActivityService"/>.</summary>
public sealed record WatchActivityRecord
{
    public required string Username { get; init; }
    public int? UserId { get; init; }
    public required string ProviderKey { get; init; }
    public MediaDomain Domain { get; init; }
    public required string ItemType { get; init; }
    public required string ItemKey { get; init; }
    public string? ParentItemKey { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public string? ImageUrl { get; init; }
    public string Source { get; init; } = WatchActivitySources.Stackarr;
    public string? Device { get; init; }
    public string? Client { get; init; }
    public string? IpAddress { get; init; }
    public string? SessionKey { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public long PositionTicks { get; init; }
    public long RuntimeTicks { get; init; }
    public double Progress { get; init; }
    public bool Completed { get; init; }
    public int TmdbId { get; init; }
    public int TvdbId { get; init; }
    public int SeasonNumber { get; init; }
    public int EpisodeNumber { get; init; }
    public long? LibraryId { get; init; }
}

/// <summary>Filters for watch-history queries.</summary>
public sealed record WatchActivityQuery
{
    public string? Username { get; init; }
    public int? UserId { get; init; }
    public string? ProviderKey { get; init; }
    public string? Source { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? Search { get; init; }
    public int Limit { get; init; } = 100;
    public int Offset { get; init; }
    public bool CompletedOnly { get; init; }
}

/// <summary>
/// Durable, per-user watch/listen history. Player modules (Jellyfin, Navidrome) record here and
/// every module can read it; plugins get the same API through DI.
/// </summary>
public sealed class WatchActivityService(
    IDbContextFactory<StackarrDbContext> dbFactory,
    TimeProvider time)
{
    public async Task<WatchActivityEntry> RecordAsync(WatchActivityRecord record, CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        string username = record.Username.Trim();
        DateTime startedAt = (record.StartedAt ?? now).ToUniversalTime();
        DateTime lastSeenAt = (record.LastSeenAt ?? now).ToUniversalTime();

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        int? userId = record.UserId ?? await ResolveUserIdAsync(db, username, ct);

        WatchActivityEntry entry;
        if (!string.IsNullOrWhiteSpace(record.SessionKey))
        {
            entry = await db.Set<WatchActivityEntry>()
                .FirstOrDefaultAsync(x => x.SessionKey == record.SessionKey, ct)
                ?? new WatchActivityEntry { SessionKey = record.SessionKey, StartedAt = startedAt };
        }
        else
        {
            entry = new WatchActivityEntry { StartedAt = startedAt };
        }

        entry.UserId = userId;
        entry.Username = username;
        entry.ProviderKey = record.ProviderKey;
        entry.Domain = record.Domain;
        entry.ItemType = record.ItemType;
        entry.ItemKey = record.ItemKey;
        entry.ParentItemKey = record.ParentItemKey ?? entry.ParentItemKey;
        entry.Title = record.Title;
        entry.Subtitle = record.Subtitle ?? entry.Subtitle;
        entry.ImageUrl = record.ImageUrl ?? entry.ImageUrl;
        entry.Source = record.Source;
        entry.Device = record.Device ?? entry.Device;
        entry.Client = record.Client ?? entry.Client;
        entry.IpAddress = record.IpAddress ?? entry.IpAddress;
        entry.LastSeenAt = lastSeenAt;
        entry.PositionTicks = Math.Max(entry.PositionTicks, record.PositionTicks);
        entry.RuntimeTicks = record.RuntimeTicks > 0 ? record.RuntimeTicks : entry.RuntimeTicks;
        entry.Progress = Math.Max(entry.Progress, Math.Clamp(record.Progress, 0, 1));
        entry.Completed = entry.Completed || record.Completed;
        entry.CompletedAt = entry.Completed ? entry.CompletedAt ?? lastSeenAt : null;
        entry.TmdbId = record.TmdbId;
        entry.TvdbId = record.TvdbId;
        entry.SeasonNumber = record.SeasonNumber;
        entry.EpisodeNumber = record.EpisodeNumber;
        entry.LibraryId = record.LibraryId;

        if (entry.Id == 0)
            db.Set<WatchActivityEntry>().Add(entry);

        await TouchStampsAsync(db, entry, ct);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>Updates the most recent open play for an item (progress/completion from a player).</summary>
    public async Task UpdateProgressAsync(
        string username,
        string providerKey,
        string itemKey,
        long positionTicks,
        long runtimeTicks,
        bool completed,
        DateTime? at = null,
        CancellationToken ct = default)
    {
        DateTime now = (at ?? time.GetUtcNow().UtcDateTime).ToUniversalTime();
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        WatchActivityEntry? entry = await db.Set<WatchActivityEntry>()
            .Where(x => x.Username == username && x.ProviderKey == providerKey && x.ItemKey == itemKey)
            .OrderByDescending(x => x.LastSeenAt ?? x.StartedAt)
            .FirstOrDefaultAsync(ct);
        if (entry is null)
            return;

        entry.LastSeenAt = now;
        entry.PositionTicks = Math.Max(entry.PositionTicks, positionTicks);
        if (runtimeTicks > 0)
            entry.RuntimeTicks = runtimeTicks;
        if (entry.RuntimeTicks > 0)
            entry.Progress = Math.Clamp(entry.PositionTicks / (double)entry.RuntimeTicks, 0, 1);
        if (completed)
        {
            entry.Completed = true;
            entry.CompletedAt ??= now;
        }
        await TouchStampsAsync(db, entry, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<WatchActivityEntry>> GetHistoryAsync(WatchActivityQuery query, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<WatchActivityEntry> q = ApplyFilters(db.Set<WatchActivityEntry>().AsNoTracking(), query);

        return await q
            .OrderByDescending(x => x.StartedAt)
            .ThenByDescending(x => x.Id)
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Clamp(query.Limit, 1, 1000))
            .ToListAsync(ct);
    }

    /// <summary>Counts history entries matching a query without loading them.</summary>
    public async Task<int> CountHistoryAsync(WatchActivityQuery query, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await ApplyFilters(db.Set<WatchActivityEntry>().AsNoTracking(), query).CountAsync(ct);
    }

    static IQueryable<WatchActivityEntry> ApplyFilters(IQueryable<WatchActivityEntry> q, WatchActivityQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.Username))
            q = q.Where(x => x.Username == query.Username);
        if (query.UserId is { } userId)
            q = q.Where(x => x.UserId == userId);
        if (!string.IsNullOrWhiteSpace(query.ProviderKey))
            q = q.Where(x => x.ProviderKey == query.ProviderKey);
        if (!string.IsNullOrWhiteSpace(query.Source))
            q = q.Where(x => x.Source == query.Source);
        if (query.From is { } from)
            q = q.Where(x => x.StartedAt >= from);
        if (query.To is { } to)
            q = q.Where(x => x.StartedAt <= to);
        if (query.CompletedOnly)
            q = q.Where(x => x.Completed);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string term = query.Search.Trim().ToLowerInvariant();
            q = q.Where(x => x.Title.ToLower().Contains(term) || (x.Subtitle != null && x.Subtitle.ToLower().Contains(term)));
        }

        return q;
    }

    /// <summary>Latest consumption for a small set of item keys (both item and parent identities match).</summary>
    public async Task<IReadOnlyDictionary<string, DateTime>> GetLastConsumedAsync(
        string providerKey,
        IReadOnlyCollection<string> itemKeys,
        CancellationToken ct = default)
    {
        Dictionary<string, DateTime> result = new(StringComparer.Ordinal);
        List<string> keys = itemKeys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count == 0)
            return result;

        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        foreach (string[] chunk in Chunk(keys, 400))
        {
            List<MediaConsumptionStamp> stamps = await db.Set<MediaConsumptionStamp>()
                .AsNoTracking()
                .Where(x => x.ProviderKey == providerKey && chunk.Contains(x.ItemKey))
                .ToListAsync(ct);
            foreach (MediaConsumptionStamp stamp in stamps)
                result[stamp.ItemKey] = stamp.LastConsumedAt;
        }
        return result;
    }

    /// <summary>All consumption stamps for one service (one query, no parameter limit).</summary>
    public async Task<IReadOnlyDictionary<string, MediaConsumptionStamp>> GetConsumptionAsync(
        string providerKey,
        CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MediaConsumptionStamp> stamps = await db.Set<MediaConsumptionStamp>()
            .AsNoTracking()
            .Where(x => x.ProviderKey == providerKey)
            .ToListAsync(ct);
        return stamps.ToDictionary(x => x.ItemKey, x => x, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> GetUsernamesAsync(CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<WatchActivityEntry>()
            .AsNoTracking()
            .Select(x => x.Username)
            .Where(x => x != "")
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
    }

    public async Task<int> PurgeBeforeAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<WatchActivityEntry>()
            .Where(x => x.StartedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<bool> HasEntryAsync(string sessionKey, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<WatchActivityEntry>().AnyAsync(x => x.SessionKey == sessionKey, ct);
    }

    static async Task TouchStampsAsync(StackarrDbContext db, WatchActivityEntry entry, CancellationToken ct)
    {
        await TouchStampAsync(db, entry, entry.ItemKey, entry.ParentItemKey, ct);
        if (!string.IsNullOrWhiteSpace(entry.ParentItemKey))
            await TouchStampAsync(db, entry, entry.ParentItemKey, null, ct);
    }

    static async Task TouchStampAsync(
        StackarrDbContext db,
        WatchActivityEntry entry,
        string itemKey,
        string? parentItemKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemKey))
            return;

        MediaConsumptionStamp? stamp = await db.Set<MediaConsumptionStamp>()
            .FirstOrDefaultAsync(x => x.ProviderKey == entry.ProviderKey && x.ItemKey == itemKey, ct);
        if (stamp is null)
        {
            stamp = new MediaConsumptionStamp
            {
                ProviderKey = entry.ProviderKey,
                ItemKey = itemKey
            };
            db.Set<MediaConsumptionStamp>().Add(stamp);
        }

        DateTime at = entry.LastSeenAt ?? entry.StartedAt;
        if (at >= stamp.LastConsumedAt)
        {
            stamp.LastConsumedAt = at;
            stamp.ItemType = entry.ItemType;
            stamp.ParentItemKey = parentItemKey ?? entry.ParentItemKey;
            stamp.Title = entry.Title;
            stamp.ImageUrl = entry.ImageUrl;
            stamp.LastUsername = entry.Username;
            stamp.LastSource = entry.Source;
            stamp.LastDevice = entry.Device;
        }
    }

    static async Task<int?> ResolveUserIdAsync(StackarrDbContext db, string username, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;
        string normalized = username.ToUpperInvariant();
        return await db.Set<StackarrUser>()
            .AsNoTracking()
            .Where(x => x.NormalizedUserName == normalized)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(ct);
    }

    static IEnumerable<string[]> Chunk(IReadOnlyList<string> values, int size)
    {
        for (int i = 0; i < values.Count; i += size)
            yield return values.Skip(i).Take(size).ToArray();
    }
}
