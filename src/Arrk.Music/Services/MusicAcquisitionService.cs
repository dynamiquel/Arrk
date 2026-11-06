using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Models;
using Arrk.Core.Services;
using Arrk.Music.Models;
using Arrk.Prowlarr;
using Arrk.Slskd;
using Arrk.Transmission;
namespace Arrk.Music.Services;

/// <summary>
/// Arrk-native music acquisition. Arrk owns the search (title-first, audio categories) and
/// the grab from two sources: torrents (Prowlarr → Transmission) for whole albums, and Soulseek
/// (slskd) for individual songs/singles. Arrk also imports (places + tags) the files itself.
/// </summary>
public class MusicAcquisitionService(
    ProwlarrService prowlarr,
    MusicLibrary library,
    TransmissionClient transmission,
    SlskdService slskd,
    DeezerService deezer,
    IDbContextFactory<ArrkDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<MusicAcquisitionService> logger,
    LiveActivityRegistry liveActivities,
    MusicNotifications? notifications = null,
    Hangfire.IBackgroundJobClient? backgroundJobs = null,
    MusicReleaseRanker? ranker = null)
{
    /// <summary>The single torrent category Arrk uses for music (matches the arrs' one-category model).</summary>
    public const string DownloadLabel = "music";

    /// <summary>Applied once a completed grab has been placed in the music library.</summary>
    public const string PlacedLabel = "music-imported";

    /// <summary>Applied to a completed grab we could not identify on Deezer; it is not imported.</summary>
    public const string UnidentifiedLabel = "music-unidentified";

    /// <summary>Applied to a completed grab whose files clearly belong to a different release than the
    /// album it was requested for (e.g. a "Halo 3" request that downloaded "Halo 3: ODST"). It is not
    /// imported under the requested album; the torrent is kept so the album it really is can reuse it.</summary>
    public const string MismatchLabel = "music-mismatch";

    /// <summary>GrabRecord source for Soulseek (slskd) grabs (torrents use "torrent").</summary>
    public const string SoulseekSource = "soulseek";

    /// <summary>Prefix for a Soulseek grab key (the slskd download folder leaf).</summary>
    internal const string SoulseekKeyPrefix = "slskd:";

    /// <summary>Where Arrk stages completed music grabs (shared by Transmission).</summary>
    public const string MusicDownloadPath = "/downloads/complete/music";

    // Library root as seen by THIS process (e.g. /media/music in the shipped container), resolved
    // from storage config. Overridable in tests.
    string libraryRoot = MusicPaths.ResolveLibraryRoot(configuration);

    // Root of the downloads tree as seen by this process (slskd writes under <downloads>/slskd).
    string downloadsRoot = MusicPaths.ResolveDownloadsRoot(configuration);

    /// <summary>Where Arrk's slskd stages its downloads (inside the shared downloads root).</summary>
    public string SoulseekRoot
    {
        get => Path.Combine(downloadsRoot, "slskd");
        set => downloadsRoot = Path.GetDirectoryName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? value;
    }

    // Releases that already failed for an album, so a re-acquire does not pick the same bad
    // torrent again. Process-local (like the download penalty state); rebuilt as failures happen.
    readonly ConcurrentDictionary<int, HashSet<string>> failedReleases = new();

    /// <summary>Library root used when placing completed downloads.</summary>
    public string LibraryRoot
    {
        get => libraryRoot;
        set => libraryRoot = value;
    }

    // Prowlarr/Newznab audio categories.
    static readonly int[] AudioCategories = [3000, 3010, 3020, 3030, 3040];

    static readonly string[] JunkTokens = ["karaoke", "tribute", "medley", "acapella", "8d"];

    // Score penalty for a release whose title appends a distinctive bare word to the matched title
    // (a likely companion album, e.g. "Halo 3 ODST" for "Halo 3"). It is a preference, not a veto:
    // a name is only a hint, and a hard rejection starves an album whose only torrent carries a bare
    // edition word. The importer's file/track check is the authority (see ReleaseMatchesAlbum).
    const int CompanionTitlePenalty = 25;

    // Version markers that mean a file is not the recording that was asked for. Rejected (token-wise)
    // when the request didn't itself ask for that version, e.g. a peer's "(Cover)" or "(Remix)" must
    // not satisfy an original, but a requested remix still matches.
    static readonly HashSet<string> VersionJunkTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "remix", "remixed", "mashup", "instrumental", "cover", "karaoke", "tribute", "medley",
        "acapella", "8d", "live"
    };

    // Tokens that mark a mono transfer ("(Mono)", "Mono Mix", "monaural"). A mono master is almost
    // never what someone wants, so these are excluded outright rather than merely scored down.
    static readonly string[] MonoTokens = ["mono", "monaural", "monophonic"];

    // Noise tokens that should not count against a title match (formats, editions, release years).
    static readonly HashSet<string> NoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "mp3", "aac", "alac", "wav", "opus", "lossless", "web", "webrip", "webdl",
        "cd", "cds", "vinyl", "remaster", "remastered", "deluxe", "edition", "special",
        "va", "various", "album", "the", "and", "a", "of", "original", "motion", "picture",
        "soundtrack", "ost", "score", "24bit", "16bit", "320", "256", "192", "vbr", "hz",
        // sample-rate / bitrate digits that show up in release names
        "24", "44", "48", "88", "96", "128", "176", "384", "hires", "khz"
    };

    static readonly char[] WordSeparators =
        [' ', '-', '_', '.', ',', ':', ';', '(', ')', '[', ']', '{', '}', '\'', '’', '`',
         '&', '/', '\\', '+', '!', '?', '"', '~', '*', '|', '<', '>', '=', '\t'];

    // Words that may legitimately follow a matched album title without changing its identity (edition,
    // packaging, format and disc words the release name may carry and NoiseTokens does not). Used by
    // the "extra title word" check below, so "Halo 3 ODST" is rejected for a "Halo 3" request while
    // "Halo 3 (Deluxe Edition)" or "Halo 3 [Disc 2]" is not.
    static readonly HashSet<string> TitleSafeTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "single", "ep", "lp", "vol", "volume", "disc", "disk", "dvd", "part", "pt", "side",
        "complete", "definitive", "collection", "compilation", "anthology", "greatest", "hits",
        "best", "box", "boxset", "set", "bonus", "track", "tracks", "feat", "ft", "mono", "stereo"
    };

    // Search

    public Task<List<MusicRelease>> SearchReleasesAsync(string artist, string title, CancellationToken ct = default) =>
        SearchReleasesAsync(artist, title, requireArtist: false, ct);

    /// <summary>
    /// Searches for releases that match an album (or a single/track title). When the requested
    /// title has no matches of its own, e.g. a single that only exists inside an album, the
    /// containing albums in our library are searched instead, most specific release first.
    /// When <paramref name="requireArtist"/> is set (track-level searches for generic song titles),
    /// only releases that also credit the artist are accepted, so a bare "Boston" query can't grab
    /// the band Boston when we asked for "Boston" by another artist.
    /// </summary>
    public async Task<List<MusicRelease>> SearchReleasesAsync(string artist, string title, bool requireArtist, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        title = MusicText.CleanTrackTitle(title);
        List<string> targets = [title];
        try
        {
            targets.AddRange(await FindContainingAlbumsAsync(artist, title, ct));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve containing albums for {Title}", title);
        }

        // Run query variants concurrently. A Prowlarr search can take about a minute, so sequential
        // variants make a track grab take minutes.
        HashSet<string> seenQueries = new(StringComparer.OrdinalIgnoreCase);
        List<Task<List<MusicRelease>>> tasks = [];
        foreach (string target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string query in BuildQueries(artist, target))
            {
                if (seenQueries.Add(query))
                    tasks.Add(SearchQueryAsync(query, artist, target, requireArtist, ct));
            }
        }
        List<MusicRelease> all = [];
        foreach (Task<List<MusicRelease>> task in tasks)
        {
            try { all.AddRange(await task); }
            catch { /* per-query failures are logged inside SearchQueryAsync */ }
        }

        return all
            .Where(r => !r.IsJunk && r.IsMatch && (!requireArtist || r.ArtistMatched))
            .GroupBy(r => !string.IsNullOrEmpty(r.InfoHash) ? r.InfoHash : r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.Score).First())
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.HasMagnet)
            .ThenByDescending(r => r.IsLossless)
            .ThenByDescending(r => r.Seeders)
            .ToList();
    }

    async Task<List<MusicRelease>> SearchTargetAsync(string artist, string target, bool requireArtist, CancellationToken ct)
    {
        List<MusicRelease> releases = [];
        foreach (string query in BuildQueries(artist, target))
        {
            if (ct.IsCancellationRequested)
                break;
            releases.AddRange(await SearchQueryAsync(query, artist, target, requireArtist, ct));

            if (releases.Any(r => r.Score >= 80 && r.HasMagnet && (!requireArtist || r.ArtistMatched)))
                break;
        }
        return releases;
    }

    async Task<List<MusicRelease>> SearchQueryAsync(string query, string artist, string target, bool requireArtist, CancellationToken ct, int? indexerId = null)
    {
        try
        {
            IEnumerable<int>? indexerIds = indexerId is > 0 ? [indexerId.Value] : null;
            List<ProwlarrSearchResult> results = await prowlarr.SearchAsync(query, AudioCategories, indexerIds, ct);
            return results.Select(r => ToRelease(r, artist, target, requireArtist)).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prowlarr search '{Query}' failed", query);
            return [];
        }
    }

    /// <summary>
    /// Streams matching releases as each query variant returns, running every variant concurrently
    /// so the first results appear as soon as the fastest query answers instead of waiting for each
    /// variant in turn. Used by the interactive search picker.
    /// </summary>
    public async IAsyncEnumerable<MusicRelease> StreamReleasesAsync(
        string artist,
        string title,
        bool requireArtist,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            yield break;

        title = MusicText.CleanTrackTitle(title);
        List<string> targets = [title];
        try
        {
            targets.AddRange(await FindContainingAlbumsAsync(artist, title, ct));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve containing albums for {Title}", title);
        }

        HashSet<string> seenQueries = new(StringComparer.OrdinalIgnoreCase);
        List<Task<List<MusicRelease>>> tasks = [];
        // Search each enabled indexer separately so fast indexers show results immediately while the
        // slow ones (e.g. The Pirate Bay) are still working, instead of waiting for Prowlarr to
        // aggregate them all.
        List<int> indexerIds = await prowlarr.GetEnabledIndexerIdsAsync(ct);
        foreach (string target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string query in BuildQueries(artist, target))
            {
                if (!seenQueries.Add(query))
                    continue;

                if (indexerIds.Count > 0)
                {
                    foreach (int indexerId in indexerIds)
                        tasks.Add(SearchQueryAsync(query, artist, target, requireArtist, ct, indexerId));
                }
                else
                {
                    tasks.Add(SearchQueryAsync(query, artist, target, requireArtist, ct));
                }
            }
        }

        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase);
        while (tasks.Count > 0 && !ct.IsCancellationRequested)
        {
            Task<List<MusicRelease>> done = await Task.WhenAny(tasks);
            tasks.Remove(done);

            List<MusicRelease> batch;
            try
            {
                batch = await done;
            }
            catch
            {
                continue;
            }

            foreach (MusicRelease release in batch
                .Where(r => r.IsMatch && (!requireArtist || r.ArtistMatched))
                .OrderByDescending(r => r.Score)
                .ThenByDescending(r => r.HasMagnet))
            {
                string key = !string.IsNullOrEmpty(release.InfoHash) ? release.InfoHash : MusicText.NormalizeKey(release.Title);
                if (yielded.Add(key))
                    yield return release;
            }
        }
    }

    static IEnumerable<string> BuildQueries(string artist, string title)
    {
        yield return title;
        if (!string.IsNullOrWhiteSpace(artist))
            yield return $"{title} {artist}";

        // Indexers often can't match symbolic titles ("÷" is stored as "divide"/"&divide;"), so also
        // try a symbol-expanded form, with and without the artist.
        string expanded = ExpandSymbols(title);
        if (!string.Equals(expanded, title, StringComparison.Ordinal))
        {
            yield return expanded;
            if (!string.IsNullOrWhiteSpace(artist))
                yield return $"{expanded} {artist}";
        }
    }

    /// <summary>Expands symbols indexers usually spell out, so title-first search can match them.</summary>
    static string ExpandSymbols(string value) =>
        value.Replace("÷", " divide ", StringComparison.Ordinal)
             .Replace("×", " x ", StringComparison.Ordinal)
             .Replace("&", " and ", StringComparison.Ordinal)
             .Replace("+", " plus ", StringComparison.Ordinal)
             .Replace("=", " equals ", StringComparison.Ordinal);

    /// <summary>
    /// Finds albums by <paramref name="artist"/> whose track list contains a track named like
    /// <paramref name="title"/> (used for singles that only exist inside a larger release).
    /// Ordered smallest-release-first so the most targeted grab wins.
    /// </summary>
    async Task<List<string>> FindContainingAlbumsAsync(string artist, string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return [];

        List<Artist> artists = await library.GetAllArtistsAsync(ct);
        Artist? match = artists.FirstOrDefault(a => string.Equals(a.Name, artist, StringComparison.OrdinalIgnoreCase))
            ?? artists.FirstOrDefault(a => string.Equals(MusicText.NormalizeKey(a.Name), MusicText.NormalizeKey(artist), StringComparison.Ordinal));
        if (match is null)
            return [];

        List<Album> albums = await library.GetArtistAlbumsAsync(match.Id, ct);
        HashSet<string> wanted = Tokens(title);
        List<(string Title, int TrackCount)> containing = [];
        foreach (Album album in albums)
        {
            if (ct.IsCancellationRequested)
                break;
            if (album.TrackCount > 0 && album.TrackFileCount >= album.TrackCount)
                continue; // already fully downloaded
            try
            {
                List<Track> tracks = await library.GetTracksAsync(album.Id, ct);
                if (tracks.Any(t => TrackMatches(t.Title, wanted)))
                    containing.Add((album.Title, Math.Max(tracks.Count, album.TotalTrackCount)));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read tracks for album {Album}", album.Title);
            }
        }

        return containing
            .OrderBy(x => x.TrackCount)
            .Select(x => x.Title)
            .Where(t => !string.Equals(t, title, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>True when the track's title contains every token of the requested (normalized) title.</summary>
    static bool TrackMatches(string trackTitle, HashSet<string> wanted) =>
        wanted.Count > 0 && wanted.All(t => Tokens(trackTitle).Contains(t));

    // Grab

    /// <summary>Searches and grabs the best candidate; returns the outcome (with the release it picked).</summary>
    public async Task<MusicGrabResult> SearchAndGrabAsync(string artist, string title, CancellationToken ct = default)
    {
        int? albumId = null;
        try
        {
            Album? album = await library.FindAlbumByTitleAsync(artist, title, ct);
            albumId = album?.Id;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve album id for {Title}", title);
        }
        return await SearchAndGrabAsync(artist, title, albumId, requireArtist: false, ct);
    }

    /// <summary>Searches and grabs the best candidate, tagging the grab with its album id.</summary>
    public Task<MusicGrabResult> SearchAndGrabAsync(string artist, string title, int? albumId, CancellationToken ct = default) =>
        SearchAndGrabAsync(artist, title, albumId, requireArtist: false, ct);

    /// <summary>
    /// Grabs the best candidate for a single track/song. Requires the release to credit the artist,
    /// so a generic title ("Boston", "Carry On") can't accidentally match an unrelated release.
    /// </summary>
    public Task<MusicGrabResult> SearchTrackAndGrabAsync(string artist, string title, CancellationToken ct = default) =>
        SearchAndGrabAsync(artist, title, albumId: null, requireArtist: true, deezerTrackId: 0, ct);

    /// <summary>Grabs the requested song, carrying its Deezer track id so the file binds to it exactly.</summary>
    public Task<MusicGrabResult> SearchTrackAndGrabAsync(string artist, string title, long deezerTrackId, CancellationToken ct = default) =>
        SearchAndGrabAsync(artist, title, albumId: null, requireArtist: true, deezerTrackId, ct);

    public Task<MusicGrabResult> SearchAndGrabAsync(string artist, string title, int? albumId, bool requireArtist, CancellationToken ct = default) =>
        SearchAndGrabAsync(artist, title, albumId, requireArtist, deezerTrackId: 0, ct);

    /// <summary>
    /// Searches and grabs the best candidate. <paramref name="deezerTrackId"/> (when known) is
    /// persisted with the grab so the importer binds the finished file to that exact song rather than
    /// guessing from the peer's folder.
    /// </summary>
    public async Task<MusicGrabResult> SearchAndGrabAsync(string artist, string title, int? albumId, bool requireArtist, long deezerTrackId, CancellationToken ct = default)
    {
        using LiveActivityScope activity = liveActivities.Begin(
            LiveActivityKind.Search, $"Searching \"{title}\"", DescribeMusicTarget(artist, source: null));
        MusicGrabResult result = await SearchAndGrabCoreAsync(artist, title, albumId, requireArtist, deezerTrackId, activity, ct);
        // Audit-only event so the Events page shows why a song/album didn't download (no notification).
        if (!result.Success && notifications is not null)
            await notifications.SearchMissedAsync(artist, string.IsNullOrWhiteSpace(title) ? artist : title, result.Message, ct);
        return result;
    }

    /// <summary>Readable line for the live activity detail, naming the source when one is being tried.</summary>
    static string DescribeMusicTarget(string artist, string? source)
    {
        string who = string.IsNullOrWhiteSpace(artist) ? "Looking for music" : $"Looking for music by {artist}";
        return source is null ? who : $"{who} · {source}";
    }

    async Task<MusicGrabResult> SearchAndGrabCoreAsync(string artist, string title, int? albumId, bool requireArtist, long deezerTrackId, LiveActivityScope activity, CancellationToken ct)
    {
        bool useTorrents = configuration.GetValue("services:music:useTorrents", true);
        bool useSoulseek = configuration.GetValue("services:music:useSoulseek", true) && slskd.IsEnabled;

        // Single tracks are Soulseek-only: a torrent that is one song is rarely indexed and matches
        // poorly, so we don't waste an indexer search on it. Torrents are used for tracks only when
        // Soulseek isn't enabled at all (a deliberate Soulseek-less setup).
        if (requireArtist && useSoulseek)
        {
            activity.Update(DescribeMusicTarget(artist, "Soulseek"));
            return await TrySoulseekAsync(artist, title, albumId, deezerTrackId, isTrack: true, ct);
        }

        // Albums (or tracks with Soulseek disabled): torrents first, Soulseek as the album fallback.
        if (useTorrents)
            activity.Update(DescribeMusicTarget(artist, "torrents via Prowlarr"));
        List<MusicRelease> releases = useTorrents
            ? ExcludeFailedReleases(await SearchReleasesAsync(artist, title, requireArtist, ct), albumId)
            : [];
        if (releases.Count == 0)
        {
            // No torrent, fall back to Soulseek for albums (a track only gets here when Soulseek is off).
            if (useSoulseek && !requireArtist)
            {
                activity.Update(DescribeMusicTarget(artist, "Soulseek"));
                MusicGrabResult soulseek = await TrySoulseekAsync(artist, title, albumId, deezerTrackId, isTrack: false, ct);
                if (soulseek.Success)
                    return soulseek;
            }
            return new MusicGrabResult(false, null, useTorrents
                ? $"No releases found for \"{title}\"."
                : $"No Soulseek release found for \"{title}\" (torrents are disabled).");
        }

        MusicRelease best = releases[0];
        if (ranker is not null)
        {
            // The ranker only reorders candidates that already passed the deterministic matcher, and
            // only when the configured mode says it can change the outcome.
            MusicRelease? aiPick = await ranker.PickAsync(
                albumId ?? 0,
                artist,
                title,
                isTrack: requireArtist,
                releases,
                ct);

            if (aiPick is not null)
                best = aiPick;
        }

        return await GrabAsync(best, albumId, ct);
    }

    /// <summary>
    /// Re-acquires an album after its download failed (stalled/slow/errored). The failed release is
    /// excluded so the same bad torrent is not grabbed again and the next-best candidate wins.
    /// Never throws.
    /// </summary>
    public async Task<MusicGrabResult> ReacquireAlbumAsync(int albumId, string? failedReleaseName, CancellationToken ct = default)
    {
        if (albumId <= 0)
            return new MusicGrabResult(false, null, "No album id to re-acquire.");

        if (!string.IsNullOrWhiteSpace(failedReleaseName))
            BlacklistRelease(albumId, failedReleaseName);

        Album? album;
        try
        {
            album = await library.GetAlbumAsync(albumId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load album {AlbumId} to re-acquire", albumId);
            return new MusicGrabResult(false, null, $"Could not load album {albumId}.");
        }

        if (album is null)
            return new MusicGrabResult(false, null, $"Album {albumId} not found.");

        logger.LogInformation("Re-acquiring \"{Album}\" by {Artist} after a failed download",
            album.Title, album.ArtistName);

        return await SearchAndGrabAsync(album.ArtistName, album.Title, albumId, requireArtist: false, ct);
    }

    /// <summary>Remembers a failed release for an album so the next search skips it (used by the
    /// torrent failure resolver before re-queueing the album).</summary>
    public void BlacklistAlbumRelease(int albumId, string releaseName) => BlacklistRelease(albumId, releaseName);

    /// <summary>Remembers a failed release for an album so it is skipped on the next search.</summary>
    void BlacklistRelease(int albumId, string releaseName)
    {
        HashSet<string> blocked = failedReleases.GetOrAdd(albumId, _ => new(StringComparer.OrdinalIgnoreCase));
        lock (blocked)
        {
            blocked.Add(MusicText.NormalizeKey(releaseName));
        }
    }

    /// <summary>Drops releases that previously failed for <paramref name="albumId"/>.</summary>
    List<MusicRelease> ExcludeFailedReleases(List<MusicRelease> releases, int? albumId)
    {
        if (albumId is not int id || id <= 0 || !failedReleases.TryGetValue(id, out HashSet<string>? blocked))
            return releases;

        string[] keys;
        lock (blocked)
        {
            keys = [.. blocked];
        }

        if (keys.Length == 0)
            return releases;

        return releases
            .Where(r => !IsFailed(r, keys))
            .ToList();
    }

    static bool IsFailed(MusicRelease release, string[] blockedKeys)
    {
        string titleKey = MusicText.NormalizeKey(release.Title);
        if (blockedKeys.Contains(titleKey, StringComparer.OrdinalIgnoreCase))
            return true;

        string hashKey = MusicText.NormalizeKey(release.InfoHash);
        return hashKey.Length > 0 && blockedKeys.Contains(hashKey, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Arrk-native acquisition seam (used by the music request handler).</summary>
    public async Task AcquireAlbumAsync(string artist, string album, CancellationToken ct = default)
    {
        MusicGrabResult result = await SearchAndGrabAsync(artist, album, ct);
        if (!result.Success)
            logger.LogDebug("No Arrk grab for \"{Album}\": {Message}", album, result.Message);
    }

    /// <summary>Adds a release to Transmission (Arrk owns the grab; Arrk imports it later).</summary>
    public async Task<MusicGrabResult> GrabAsync(MusicRelease release, int? albumId = null, CancellationToken ct = default)
    {
        string uri = !string.IsNullOrWhiteSpace(release.MagnetUrl) ? release.MagnetUrl : release.DownloadUrl;
        if (string.IsNullOrWhiteSpace(uri))
            return new MusicGrabResult(false, release, "The release has no usable download link.");

        try
        {
            List<TransmissionTorrent> existing = await transmission.GetTorrents(ct: ct);
            TransmissionTorrent? duplicate = existing.FirstOrDefault(t =>
                !string.IsNullOrEmpty(release.InfoHash) &&
                string.Equals(t.HashString, release.InfoHash, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                bool reused = await TryReuseCompletedTorrentAsync(duplicate, albumId, ct);
                await RecordGrabAsync(albumId, release, duplicate.Id, ct);
                await NotifyGrabbedAsync(release, albumId, ct);
                return new MusicGrabResult(true, release,
                    reused ? $"Reusing the completed download \"{release.Title}\"." : $"Already downloading \"{release.Title}\".",
                    duplicate.Id);
            }

            List<string> labels = [DownloadLabel];

            long id = await transmission.AddTorrentByUri(new Uri(uri), MusicDownloadPath, [.. labels], ct);
            await RecordGrabAsync(albumId, release, id, ct);
            logger.LogInformation("Grabbed \"{Title}\" from {Indexer} ({Seeders} seeders) as torrent {Id}",
                release.Title, release.Indexer, release.Seeders, id);
            await NotifyGrabbedAsync(release, albumId, ct);
            return new MusicGrabResult(true, release, $"Grabbed \"{release.Title}\" from {release.Indexer}.", id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Grab failed for \"{Title}\"", release.Title);
            return new MusicGrabResult(false, release, $"Grab failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A torrent we already hold is being requested for a different album. When the completed download
    /// really is the requested album, reuse it: clear the (mis)placements it left on the album it was
    /// previously bound to, rebind the grab to the requested album and re-import it, instead of
    /// dead-ending on "already downloading". Never steals a torrent whose files are not this album.
    /// Returns true when the download was reused.
    /// </summary>
    async Task<bool> TryReuseCompletedTorrentAsync(TransmissionTorrent duplicate, int? albumId, CancellationToken ct)
    {
        if (albumId is not int requested || requested <= 0 || duplicate.PercentDone < 1.0)
            return false;

        string hash = (duplicate.HashString ?? "").Trim().ToLowerInvariant();
        if (hash.Length == 0)
            return false;

        GrabRecord? grab = await GrabForTorrentAsync(duplicate, ct);
        if (grab is not null && grab.AlbumId == requested)
            return false;

        Album? album = await library.GetAlbumAsync(requested, ct);
        if (album is null)
            return false;

        // Only reuse when the files actually are this album, otherwise the torrent belongs where it is.
        List<string> audio = [.. EnumerateAudio(ResolveFolder(duplicate))];
        if (!ReleaseMatchesAlbum(album, audio))
            return false;

        if (grab is not null && grab.AlbumId > 0 && grab.AlbumId != requested)
            await ClearMisplacedPlacementsAsync(grab.AlbumId, audio, ct);

        await RebindTorrentGrabAsync(hash, requested, ct);
        await transmission.SetTorrentLabels(duplicate.Id, [DownloadLabel], ct);
        logger.LogInformation("Reusing the completed download \"{Name}\" (torrent {Hash}) for album {AlbumId}",
            duplicate.Name, hash, requested);
        return true;
    }

    /// <summary>Points a torrent's grab at an album and marks it <c>Grabbed</c> so the importer re-runs
    /// for the new owner (creating the grab when the torrent had none).</summary>
    async Task RebindTorrentGrabAsync(string hash, int albumId, CancellationToken ct)
    {
        long deezerAlbumId = await LibraryAlbumDeezerIdAsync(albumId, ct);
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            GrabRecord? record = await db.Set<GrabRecord>()
                .FirstOrDefaultAsync(g => g.Source == GrabSource.Torrent && g.Identifier == hash, ct);
            if (record is null)
            {
                db.Set<GrabRecord>().Add(new GrabRecord
                {
                    AlbumId = albumId,
                    Source = GrabSource.Torrent,
                    Identifier = hash,
                    State = GrabState.Grabbed,
                    DeezerAlbumId = deezerAlbumId,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                record.AlbumId = albumId;
                record.DeezerAlbumId = deezerAlbumId;
                record.State = GrabState.Grabbed;
                record.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not rebind torrent {Hash} to album {AlbumId}", hash, albumId);
        }
    }

    /// <summary>
    /// Clears the wrong album a release was placed into before it is rebound to the album that owns
    /// it. Only clears when every placed track of the other album corresponds to this release's file
    /// count, so an album that also holds other sources is left untouched. The misplaced files are
    /// deleted (best effort) and the tracks read as missing again.
    /// </summary>
    async Task ClearMisplacedPlacementsAsync(int albumId, IReadOnlyList<string> audio, CancellationToken ct)
    {
        try
        {
            Album? old = await library.GetAlbumAsync(albumId, ct);
            if (old is null)
                return;

            List<Track> placed = [.. old.Tracks.Where(t => t.HasFile)];
            if (placed.Count == 0)
                return;
            if (placed.Count != audio.Count)
            {
                logger.LogInformation(
                    "Torrent being rebound from \"{Album}\" has {Placed} placed track(s) but the release has {Files} file(s); leaving them in place",
                    old.Title, placed.Count, audio.Count);
                return;
            }

            foreach (Track track in placed)
            {
                if (!string.IsNullOrEmpty(track.FilePath))
                {
                    try
                    {
                        string full = Path.Combine(LibraryRoot, track.FilePath);
                        if (File.Exists(full))
                            File.Delete(full);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "Could not delete the misplaced file for track {TrackId}", track.Id);
                    }
                }
                await library.ClearTrackFileAsync(track.Id, ct);
            }
            logger.LogInformation("Cleared {Count} misplaced track(s) from \"{Album}\" before reusing the download",
                placed.Count, old.Title);
            InvalidateTechnicalCache();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not clear misplaced tracks for album {AlbumId}", albumId);
        }
    }

    /// <summary>Emits a "music grabbed" notification with best-effort album artwork (never throws).</summary>
    async Task NotifyGrabbedAsync(MusicRelease release, int? albumId, CancellationToken ct)
    {
        if (notifications is null)
            return;

        Album? album = null;
        if (albumId is > 0)
        {
            try
            {
                album = await library.GetAlbumAsync(albumId.Value, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not load album {Id} for grab notification", albumId);
            }
        }

        await notifications.AlbumGrabbedAsync(album, release.Title, ct);
    }

    // Soulseek grab

    /// <summary>
    /// Grabs a Soulseek release (slskd) for the interactive picker: enqueues the files and records
    /// the target album/track so a completed download is placed into the right album rather than as
    /// a bare, metadata-less folder. Returns the outcome for the picker's toast.
    /// </summary>
    public async Task<MusicGrabResult> GrabSoulseekAsync(
        string artist,
        string title,
        int? albumId,
        string peer,
        IReadOnlyList<SlskdFile> files,
        string releaseTitle,
        CancellationToken ct = default) =>
        await GrabSoulseekAsync(artist, title, albumId, peer, files, releaseTitle, deezerTrackId: 0, ct);

    /// <summary>As <see cref="GrabSoulseekAsync(string,string,int?,string,IReadOnlyList{SlskdFile},string,CancellationToken)"/>,
    /// carrying the requested Deezer track id so the file binds to that exact song on import.</summary>
    public async Task<MusicGrabResult> GrabSoulseekAsync(
        string artist,
        string title,
        int? albumId,
        string peer,
        IReadOnlyList<SlskdFile> files,
        string releaseTitle,
        long deezerTrackId,
        CancellationToken ct = default)
    {
        if (!slskd.IsEnabled)
            return new MusicGrabResult(false, null, "Soulseek is not enabled.");

        return await EnqueueSoulseekAsync(artist, title, albumId, peer, releaseTitle, files, deezerTrackId, ct);
    }

    /// <summary>Enqueues a Soulseek download and records its target so the importer can place it.</summary>
    async Task<MusicGrabResult> EnqueueSoulseekAsync(
        string artist,
        string title,
        int? albumId,
        string peer,
        string releaseTitle,
        IReadOnlyList<SlskdFile> files,
        long deezerTrackId,
        CancellationToken ct)
    {
        if (files.Count == 0)
            return new MusicGrabResult(false, null, "This Soulseek result has no files to download.");

        try
        {
            int? targetAlbum = await ResolveSoulseekAlbumAsync(artist, title, albumId, ct);

            // A single requested song is keyed by folder + file name (generic folder names collide
            // across unrelated grabs); an album grab stays keyed by its folder.
            string grabKey = files.Count == 1
                ? SoulseekFileKey(files[0].Filename)
                : SoulseekGrabKey(files[0].Filename);

            // Never re-grab a source that already failed for the same album, including songs we
            // couldn't bind to an album (album id 0), which otherwise looped on the same bad source.
            if (await IsSoulseekBlacklistedAsync(targetAlbum ?? 0, deezerTrackId, grabKey, ct))
                return new MusicGrabResult(false, null, "That Soulseek source previously failed for this album.");

            if (!await slskd.EnqueueAsync(peer, files, ct))
                return new MusicGrabResult(false, null, "slskd refused the download.");

            // Best-effort (lost on restart): remember which song each file was grabbed for, so the
            // importer can trust the requested identity over the peer's folder. Bounded so a
            // long-running process can't grow it without limit.
            if (soulseekIntents.Count > 10_000)
                soulseekIntents.Clear();
            foreach (SlskdFile file in files)
                soulseekIntents[Path.GetFileName(file.Filename.Replace('\\', '/'))] = (artist, title, deezerTrackId);

            await RecordSoulseekGrabAsync(targetAlbum, grabKey, releaseTitle, deezerTrackId, ct);
            logger.LogInformation("Queued {Count} Soulseek file(s) for \"{Title}\" from peer {Peer}",
                files.Count, title, peer);

            if (notifications is not null)
            {
                string quality = files.Any(f => LooksLossless(f.Filename.Replace('\\', '/'))) ? "lossless" : "lossy";
                await notifications.SoulseekGrabbedAsync(artist, title, peer, quality, ct);
            }

            return new MusicGrabResult(true, null, $"Queued {files.Count} file(s) from Soulseek peer {peer}.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Soulseek grab failed for \"{Title}\"", title);
            return new MusicGrabResult(false, null, $"Grab failed: {ex.Message}");
        }
    }

    /// <summary>Resolves the album a Soulseek grab belongs to (supplied id, exact album, or containing album).</summary>
    async Task<int?> ResolveSoulseekAlbumAsync(string artist, string title, int? albumId, CancellationToken ct)
    {
        if (albumId is > 0)
            return albumId;

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
            return null;

        try
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                Album? album = await library.FindAlbumByTitleAsync(artist, title, ct);
                if (album is not null)
                    return album.Id;

                // A song title: find the album we hold that contains a track with this name.
                List<string> containing = await FindContainingAlbumsAsync(artist, title, ct);
                foreach (string name in containing)
                {
                    Album? match = await library.FindAlbumByTitleAsync(artist, name, ct);
                    if (match is not null)
                        return match.Id;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve the Soulseek target album for {Title}", title);
        }

        // No safe association: leave it unattached and let the importer place it as its own folder.
        return null;
    }

    /// <summary>True when there is a failed Soulseek grab for the same source key.
    /// Pass album id 0 for a song that isn't bound to an album.</summary>
    /// <summary>
    /// The sources already blacklisted for this request (recent failures only). A song grab matches by
    /// its requested Deezer track id, an album grab by its album, so this is never an artist/title ban.
    /// </summary>
    async Task<HashSet<string>> GetSoulseekBlacklistAsync(int albumId, long deezerTrackId, CancellationToken ct)
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            DateTime since = DateTime.UtcNow.AddHours(-GetConfigDouble("services:slskd.blacklistHours", 168));
            List<string> ids = await db.Set<GrabRecord>().AsNoTracking()
                .Where(g => g.Source == SoulseekSource
                    && g.State == GrabState.Failed
                    && g.UpdatedAt >= since
                    && (deezerTrackId > 0 ? g.DeezerTrackId == deezerTrackId : g.AlbumId == albumId))
                .Select(g => g.Identifier)
                .ToListAsync(ct);
            foreach (string id in ids)
            {
                if (!string.IsNullOrEmpty(id))
                    keys.Add(id);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load the Soulseek blacklist for {Title}", deezerTrackId);
        }
        return keys;
    }

    /// <summary>
    /// True when a Soulseek source failed for the same request before. Scoped tightly so it never bans
    /// an artist or a title: a song grab is keyed by the requested Deezer track id (a source rejected
    /// for that song stays usable for any other request) and an album grab by its album. Expires after
    /// <c>services:slskd.blacklistHours</c> so a source can be tried again later.
    /// </summary>
    async Task<bool> IsSoulseekBlacklistedAsync(int albumId, long deezerTrackId, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            DateTime since = DateTime.UtcNow.AddHours(-GetConfigDouble("services:slskd.blacklistHours", 168));
            return await db.Set<GrabRecord>().AnyAsync(
                g => g.Source == SoulseekSource
                    && g.Identifier == key
                    && g.State == GrabState.Failed
                    && g.UpdatedAt >= since
                    && (deezerTrackId > 0 ? g.DeezerTrackId == deezerTrackId : g.AlbumId == albumId), ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not check the Soulseek blacklist for {Key}", key);
            return false;
        }
    }

    /// <summary>Persists the target of a Soulseek grab (keyed by the file/folder key).</summary>
    async Task RecordSoulseekGrabAsync(int? albumId, string key, string releaseTitle, long deezerTrackId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            GrabRecord? existing = await db.Set<GrabRecord>()
                .FirstOrDefaultAsync(g => g.Source == SoulseekSource && g.Identifier == key, ct);

            if (existing is not null)
            {
                // A re-grab for the same source: refresh the album/track and clear a previous failure.
                if (albumId is > 0)
                    existing.AlbumId = albumId.Value;
                if (deezerTrackId > 0)
                    existing.DeezerTrackId = deezerTrackId;
                if (existing.DeezerAlbumId <= 0)
                    existing.DeezerAlbumId = await LibraryAlbumDeezerIdAsync(albumId, ct);
                existing.ReleaseTitle = string.IsNullOrWhiteSpace(releaseTitle) ? existing.ReleaseTitle : releaseTitle;
                existing.State = GrabState.Grabbed;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                db.Set<GrabRecord>().Add(new GrabRecord
                {
                    AlbumId = albumId ?? 0,
                    Source = SoulseekSource,
                    Identifier = key,
                    ReleaseTitle = releaseTitle,
                    DeezerTrackId = deezerTrackId,
                    DeezerAlbumId = await LibraryAlbumDeezerIdAsync(albumId, ct),
                    State = GrabState.Grabbed,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record Soulseek grab {Key}", key);
        }
    }

    /// <summary>
    /// The stable key slskd uses to fold a download into a folder: the leaf of the remote folder
    /// (slskd's <c>SOURCE_DIRECTORY</c>), or the file name when the file sits at the share root.
    /// </summary>
    internal static string SoulseekGrabKey(string remotePath)
    {
        string value = (remotePath ?? "").Replace('\\', '/').Trim().Trim('/');
        if (value.Length == 0)
            return "";
        string dir = Path.GetDirectoryName(value)?.Replace('\\', '/') ?? "";
        string leaf = Path.GetFileName(dir.TrimEnd('/'));
        if (string.IsNullOrEmpty(leaf))
            leaf = Path.GetFileName(value);
        string key = SoulseekKeyPrefix + leaf.ToLowerInvariant();
        return key.Length > 200 ? key[..200] : key;
    }

    /// <summary>
    /// The grab key for a single requested song: the remote folder leaf <em>and</em> the file name.
    /// Peers share songs inside generic folders ("music", "Disc 1"), so the folder leaf alone
    /// collides across unrelated grabs; including the file name keeps every single-file grab distinct.
    /// </summary>
    internal static string SoulseekFileKey(string remotePath)
    {
        string value = (remotePath ?? "").Replace('\\', '/').Trim().Trim('/');
        if (value.Length == 0)
            return "";
        string dir = Path.GetDirectoryName(value)?.Replace('\\', '/') ?? "";
        string leaf = Path.GetFileName(dir.TrimEnd('/'));
        string file = Path.GetFileName(value);
        string composite = string.IsNullOrEmpty(leaf) ? file : $"{leaf}/{file}";
        string key = SoulseekKeyPrefix + "f:" + composite.ToLowerInvariant();
        return key.Length > 200 ? key[..200] : key;
    }

    // Import

    /// <summary>
    /// Places every completed Arrk-grabbed music download into the music library (hard-linked, so
    /// seeding is unaffected and no extra disk is used), then relabels it so it isn't re-imported.
    ///
    /// We place the files ourselves: partial releases (e.g. a 31-file rip of a 32-track album) and
    /// rips without usable tags are still added with correct artist/album/title metadata.
    /// </summary>
    public async Task<int> ImportCompletedAsync(CancellationToken ct = default)
    {
        List<TransmissionTorrent> torrents;
        try
        {
            torrents = await transmission.GetTorrents(ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list torrents for music import");
            torrents = [];
        }

        int imported = 0;
        foreach (TransmissionTorrent torrent in torrents)
        {
            if (ct.IsCancellationRequested)
                break;
            if (torrent.Labels is null || !torrent.Labels.Contains(DownloadLabel))
                continue;
            if (torrent.PercentDone < 1.0)
                continue;

            string folder = ResolveFolder(torrent);
            try
            {
                // Identify first: only releases matched to a Deezer album/track are imported, into that
                // release's canonical folder (never the download's own structure).
                Album? album = await ResolveAlbumForTorrentAsync(torrent, ct);
                if (album is null)
                {
                    logger.LogInformation("Not importing \"{Name}\": it isn't identified on Deezer", torrent.Name);
                    await transmission.SetTorrentLabels(torrent.Id, [UnidentifiedLabel], ct);
                    continue;
                }

                // The requested album is a hint. Confirm the download's own files belong to it before
                // tagging and filing them under that album.
                string placeRoot = Directory.Exists(folder) ? ChooseAlbumFolder(folder, album.Title) : folder;
                List<string> audio = [.. EnumerateAudio(placeRoot)];
                if (!ReleaseMatchesAlbum(album, audio))
                {
                    await HandleAlbumMismatchAsync(torrent, album, ct);
                    continue;
                }

                using LiveActivityScope importing = liveActivities.Begin(
                    LiveActivityKind.Import, $"Importing \"{album.Title}\"",
                    string.IsNullOrWhiteSpace(album.ArtistName) ? "Adding to your library" : $"Adding {album.ArtistName} to your library");
                List<(int TrackId, string FilePath)> placed = await PlaceAlbumAsync(album, audio, null, move: false, ct);
                await library.MarkTracksPresentAsync(album.Id, placed, ct);
                await transmission.SetTorrentLabels(torrent.Id, [PlacedLabel], ct);

                if (placed.Count > 0)
                {
                    await MarkImportedAsync(torrent, ct);
                    await library.CompleteAcquireAsync(album.Id, 0, ct);
                    logger.LogInformation("Imported \"{Album}\" ({Count} file(s)) by {Artist}",
                        album.Title, placed.Count, album.ArtistName);
                    imported++;
                    if (notifications is not null)
                        await notifications.ImportedAsync(album, torrent.Name, ResolvePlacedTracks(album, placed), ct);
                }
                else
                {
                    logger.LogDebug("Nothing to place for \"{Name}\" (already in the library)", torrent.Name);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not import \"{Name}\"", torrent.Name);
            }
        }

        imported += await ImportSoulseekAsync(ct);
        if (imported > 0)
        {
            library.InvalidateAvailability();
            InvalidateTechnicalCache();
        }
        return imported;
    }

    /// <summary>
    /// Resolves the library tracks a placed set of files landed on, so an import can be reported
    /// accurately (a single track is a song, several are an album).
    /// </summary>
    static List<Track> ResolvePlacedTracks(Album album, IReadOnlyList<(int TrackId, string FilePath)> placed)
    {
        if (placed.Count == 0 || album.Tracks.Count == 0)
            return [];

        HashSet<int> ids = [.. placed.Select(p => p.TrackId)];
        return [.. album.Tracks.Where(t => ids.Contains(t.Id)).OrderBy(t => t.AbsoluteTrackNumber)];
    }

    /// <summary>
    /// Resolves the Deezer album a completed torrent belongs to: the exact track/album the grab
    /// recorded, the album we already hold, or, with no usable grab, an identification from the
    /// release name. Never invents an unidentified album.
    /// </summary>
    async Task<Album?> ResolveAlbumForTorrentAsync(TransmissionTorrent torrent, CancellationToken ct)
    {
        GrabRecord? grab = await GrabForTorrentAsync(torrent, ct);
        if (grab is not null)
        {
            if (grab.DeezerTrackId > 0)
            {
                (int trackAlbumId, _) = await EnsureDeezerTrackAlbumAsync(grab.DeezerTrackId, [], ct);
                if (trackAlbumId > 0)
                    return await library.GetAlbumAsync(trackAlbumId, ct);
            }
            if (grab.AlbumId > 0)
            {
                Album? held = await library.GetAlbumAsync(grab.AlbumId, ct);
                if (held is not null)
                    return held;
            }
        }

        (string artist, string album) = ParseAlbumFolder(MusicText.CleanReleaseName(torrent.Name));
        Album? identified = await EnsureAlbumForDownloadAsync(artist, album, [], ct);
        if (identified is not null && grab is not null)
            await AssignGrabAlbumAsync(torrent.HashString, identified.Id, ct);
        return identified;
    }

    async Task<GrabRecord?> GrabForTorrentAsync(TransmissionTorrent torrent, CancellationToken ct)
    {
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            string hash = (torrent.HashString ?? "").Trim().ToLowerInvariant();
            return await db.Set<GrabRecord>().AsNoTracking()
                .FirstOrDefaultAsync(g => g.Source == "torrent" && g.Identifier == hash, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the grab for torrent {Hash}", torrent.HashString);
            return null;
        }
    }

    // Canonical placement

    /// <summary>
    /// Places a completed release into its album's canonical library folder
    /// (<c>Artist/Album/NN - Title.ext</c>), tagging each file with the album's real metadata plus the
    /// Deezer ids (so the file can be re-identified later), and writes the album cover. Torrent files
    /// are hard-linked so seeding continues; Soulseek files are moved. Files that don't map to a track
    /// on the identified release are skipped, Arrk only keeps music it can identify.
    /// </summary>
    public Task<List<(int TrackId, string FilePath)>> PlaceAlbumAsync(
        Album album, string sourcePath, string? artistOverride, bool move, CancellationToken ct,
        long requestedTrackDeezerId = 0)
    {
        string root = Directory.Exists(sourcePath) ? ChooseAlbumFolder(sourcePath, album.Title) : sourcePath;
        return PlaceAlbumAsync(album, [.. EnumerateAudio(root)], artistOverride, move, ct, requestedTrackDeezerId);
    }

    /// <summary>As <see cref="PlaceAlbumAsync(Album,string,string?,bool,CancellationToken)"/>
    /// for a caller that already has the exact staged files (a Soulseek grab group).
    /// <paramref name="requestedTrackDeezerId"/> (when known) binds a single-file grab to that exact
    /// track instead of guessing from the file name.</summary>
    public async Task<List<(int TrackId, string FilePath)>> PlaceAlbumAsync(
        Album album, IReadOnlyList<string> audio, string? artistOverride, bool move, CancellationToken ct,
        long requestedTrackDeezerId = 0)
    {
        (List<(int TrackId, string FilePath)> placed, _) =
            await PlaceAlbumAuditedAsync(album, audio, artistOverride, move, ct, requestedTrackDeezerId);
        return placed;
    }

    /// <summary>
    /// Places a release and also reports why a file was rejected (when one was), so the caller can turn
    /// a rejected single-song download into a blacklist + re-search with a readable reason.
    /// </summary>
    async Task<(List<(int TrackId, string FilePath)> Placed, string? RejectReason)> PlaceAlbumAuditedAsync(
        Album album, IReadOnlyList<string> audio, string? artistOverride, bool move, CancellationToken ct,
        long requestedTrackDeezerId = 0)
    {
        if (audio.Count == 0)
            return ([], null);

        string? rejectReason = null;
        string destDir = Path.Combine(LibraryRoot, AlbumFolderName(album));
        Directory.CreateDirectory(destDir);

        List<Track> ordered = [.. album.Tracks
            .Where(t => t.Id != 0)
            .OrderBy(t => t.MediumNumber).ThenBy(t => t.AbsoluteTrackNumber).ThenBy(t => t.Id)];
        bool multiDisc = ordered.Select(t => t.MediumNumber).Distinct().Count() > 1;

        // A single requested song is an identity, not a title guess: when the grab recorded the Deezer
        // track id, bind the file to that exact track. An album can carry two tracks with the same
        // cleaned title (The Lion King's "Can You Feel the Love Tonight" and "… (End Title)"), and a
        // title match would always pick the first, so the requested "(End Title)" could never land.
        Track? requestedTrack = audio.Count == 1 && requestedTrackDeezerId > 0
            ? ordered.FirstOrDefault(t => t.DeezerId == requestedTrackDeezerId)
            : null;

        // Probe the one file up front: its length both disambiguates a title tie and is the duration
        // check's actual value, so a single-file import runs ffprobe once, not twice.
        MusicTechnical singleProbe = new();
        if (audio.Count == 1)
        {
            try { singleProbe = await ProbeAsync(audio[0]); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not probe {File}", Path.GetFileName(audio[0])); }
        }

        List<(int TrackId, string FilePath)> placed = [];
        HashSet<int> used = [];
        for (int i = 0; i < audio.Count; i++)
        {
            string file = audio[i];
            Track? track;
            if (audio.Count == 1 && requestedTrackDeezerId > 0)
            {
                // A single requested song is an identity, not a title. Bind it to the exact requested
                // track, and reject the release when it does not carry that track rather than placing
                // the file under a lookalike track.
                track = requestedTrack;
                if (track is null)
                {
                    rejectReason = $"\"{Path.GetFileName(file)}\" is not the requested track on \"{album.Title}\"";
                    logger.LogWarning(
                        "Rejecting \"{File}\": \"{Album}\" by {Artist} does not contain the requested track (Deezer id {TrackId})",
                        Path.GetFileName(file), album.Title, album.ArtistName, requestedTrackDeezerId);
                    continue;
                }
            }
            else
            {
                track = MatchTrack(ordered, used, file, i, multiDisc, singleProbe.DurationSeconds);
            }
            if (track is null)
            {
                logger.LogDebug("Skipping {File}: no track on \"{Album}\" matches it", file, album.Title);
                continue;
            }

            // A single requested song must actually be that song. Reject a file whose duration is
            // clearly not the Deezer track's, a different recording, cover or remix picked from an
            // unrelated release, so it is re-acquired instead of imported under this track's identity.
            if (audio.Count == 1 && track.DurationSeconds > 0
                && singleProbe.DurationSeconds > 0
                && !DurationsClose(singleProbe.DurationSeconds, track.DurationSeconds))
            {
                string tags = DescribeTags(singleProbe);
                rejectReason = $"\"{Path.GetFileName(file)}\" is {singleProbe.DurationSeconds:0}s but \"{track.Title}\" is {track.DurationSeconds}s (wrong version){tags}";
                logger.LogWarning("Rejecting \"{File}\": {Actual:0}s does not match \"{Title}\" ({Expected}s){Tags}",
                    Path.GetFileName(file), singleProbe.DurationSeconds, track.Title, track.DurationSeconds, tags);
                continue;
            }
            used.Add(track.Id);

            string destPath = Path.Combine(destDir, TrackFileName(track, multiDisc, Path.GetExtension(file)));
            if (!File.Exists(destPath))
                MoveOrLink(file, destPath, move);

            await TagFileAsync(destPath, new TrackTag(
                artistOverride ?? album.ArtistName,
                album.ArtistName,
                album.Title,
                track.Title,
                track.AbsoluteTrackNumber > 0 ? track.AbsoluteTrackNumber : track.TrackNumber,
                track.MediumNumber,
                album.Tracks.Count,
                album.ReleaseDate?.Year is > 0 and < 3000 ? album.ReleaseDate!.Value.Year.ToString() : "",
                track.DeezerId,
                album.DeezerId));
            placed.Add((track.Id, Path.GetRelativePath(LibraryRoot, destPath)));
        }

        if (placed.Count > 0)
            await WriteAlbumCoverAsync(album, destDir, ct);
        return (placed, rejectReason);
    }

    static IEnumerable<string> EnumerateAudio(string sourcePath)
    {
        if (File.Exists(sourcePath))
            return IsAudioFile(sourcePath) ? [sourcePath] : [];
        if (Directory.Exists(sourcePath))
            return Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).Where(IsAudioFile);
        return [];
    }

    /// <summary>Maps one downloaded file to a track on the release (title first, then disc+number).</summary>
    static Track? MatchTrack(
        IReadOnlyList<Track> ordered, HashSet<int> used, string file, int index, bool multiDisc,
        double preferDurationSeconds = 0)
    {
        string baseName = Path.GetFileNameWithoutExtension(file);
        Track? track = MatchTrackByTitle(ordered, used, baseName, preferDurationSeconds);
        if (track is null)
        {
            int number = ParseTrackNumber(baseName);
            if (number <= 0)
                number = index + 1;
            int medium = ParseMedium(Path.GetFileName(file));
            track = (multiDisc
                    ? ordered.FirstOrDefault(t => !used.Contains(t.Id) && t.MediumNumber == medium && t.AbsoluteTrackNumber == number)
                    : ordered.FirstOrDefault(t => !used.Contains(t.Id) && t.AbsoluteTrackNumber == number))
                ?? ordered.FirstOrDefault(t => !used.Contains(t.Id) && t.TrackNumber == number);
        }
        return track ?? ordered.ElementAtOrDefault(index);
    }

    /// <summary>The canonical library folder for an album: <c>Artist/Album</c>.</summary>
    public static string AlbumFolderName(Album album)
    {
        string artist = string.IsNullOrWhiteSpace(album.ArtistName) ? "Unknown Artist" : album.ArtistName;
        string title = string.IsNullOrWhiteSpace(album.Title) ? "Unknown Album" : album.Title;
        return Path.Combine(SafeFolderName(artist), SafeFolderName(title));
    }

    /// <summary>The canonical file name for a track: <c>NN - Title.ext</c> (or <c>D-NN - Title.ext</c>).</summary>
    public static string TrackFileName(Track track, bool multiDisc, string extension)
    {
        int number = track.AbsoluteTrackNumber > 0 ? track.AbsoluteTrackNumber : track.TrackNumber;
        if (number <= 0)
            number = 1;
        string title = string.IsNullOrWhiteSpace(track.Title) ? $"Track {number:00}" : track.Title;
        string prefix = multiDisc && track.MediumNumber > 1 ? $"{track.MediumNumber}-{number:00}" : $"{number:00}";
        return SafeFileName($"{prefix} - {title}{extension}");
    }

    static void MoveOrLink(string source, string dest, bool move)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (move)
        {
            try
            {
                File.Move(source, dest);
                return;
            }
            catch
            {
                // Different volume, or a clash: fall through to copy (and remove the source below).
            }
        }
        if (!TryCreateHardLink(source, dest))
            File.Copy(source, dest, overwrite: false);
        if (move && File.Exists(source))
        {
            try { File.Delete(source); } catch { /* best effort */ }
        }
    }

    static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Writes the Deezer album cover beside the files as <c>cover.jpg</c> so Navidrome shows
    /// artwork even for players that don't read embedded art.</summary>
    async Task WriteAlbumCoverAsync(Album album, string albumFolder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(album.PosterUrl))
            return;
        string cover = Path.Combine(albumFolder, "cover.jpg");
        if (File.Exists(cover))
            return;
        try
        {
            byte[] bytes = await CoverHttp.GetByteArrayAsync(album.PosterUrl, ct);
            if (bytes.Length > 0)
                await File.WriteAllBytesAsync(cover, bytes, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not write cover art for \"{Album}\"", album.Title);
        }
    }

    static string SafeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (safe.Length == 0)
            return "track";
        if (safe.Length > 150)
        {
            string extension = Path.GetExtension(safe);
            string stem = Path.GetFileNameWithoutExtension(safe);
            int keep = Math.Max(1, 150 - extension.Length);
            safe = stem[..Math.Min(stem.Length, keep)] + extension;
        }
        return safe;
    }

    /// <summary>Records a grab so the album can be resolved on import without a per-album torrent label.</summary>
    async Task RecordGrabAsync(int? albumId, MusicRelease release, long torrentId, CancellationToken ct)
    {
        try
        {
            string identifier = (!string.IsNullOrEmpty(release.InfoHash) ? release.InfoHash : release.MagnetUrl).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(identifier))
                return;

            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            if (await db.Set<GrabRecord>().AnyAsync(g => g.Identifier == identifier, ct))
                return;

            db.Set<GrabRecord>().Add(new GrabRecord
            {
                AlbumId = albumId ?? 0,
                Source = "torrent",
                Identifier = identifier,
                ReleaseTitle = release.Title,
                State = "Grabbed",
                TorrentId = torrentId,
                DeezerAlbumId = await LibraryAlbumDeezerIdAsync(albumId, ct),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not record grab for {Title}", release.Title);
        }
    }

    /// <summary>The Deezer id of a library album (0 when unknown), used to tag grabs for discovery.</summary>
    async Task<long> LibraryAlbumDeezerIdAsync(int? albumId, CancellationToken ct)
    {
        if (albumId is not > 0)
            return 0;
        try
        {
            Album? album = await library.GetAlbumAsync(albumId.Value, ct);
            return album?.DeezerId ?? 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the Deezer id for album {AlbumId}", albumId);
            return 0;
        }
    }

    /// <summary>Points a torrent's grab record at an album (creating one if the torrent had none).</summary>
    async Task AssignGrabAlbumAsync(string hash, int albumId, CancellationToken ct)
    {
        try
        {
            string key = (hash ?? "").Trim().ToLowerInvariant();
            if (key.Length == 0 || albumId <= 0)
                return;

            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            GrabRecord? record = await db.Set<GrabRecord>().FirstOrDefaultAsync(g => g.Identifier == key, ct);
            if (record is null)
            {
                db.Set<GrabRecord>().Add(new GrabRecord
                {
                    AlbumId = albumId,
                    Source = "torrent",
                    Identifier = key,
                    State = "Grabbed",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                record.AlbumId = albumId;
                record.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not assign album {AlbumId} to torrent {Hash}", albumId, hash);
        }
    }

    async Task MarkImportedAsync(TransmissionTorrent torrent, CancellationToken ct)
    {
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            string hash = (torrent.HashString ?? "").Trim().ToLowerInvariant();
            GrabRecord? record = await db.Set<GrabRecord>()
                .FirstOrDefaultAsync(g => g.Source == "torrent" && g.Identifier == hash, ct);
            if (record is null)
                return;
            record.State = "Imported";
            record.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            if (ranker is not null)
                await ranker.ResolveOutcomeAsync(record.AlbumId, torrent.Name, "imported", ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not mark the grab imported for torrent {Hash}", torrent.HashString);
        }
    }

    /// <summary>
    /// A completed download whose files are clearly a different release than the album it was requested
    /// for. It is not imported under the requested album: the torrent is relabelled so the importer stops
    /// retrying it (keeping it so the album it really is can reuse it), the release is blacklisted for
    /// this album, and the album is re-queued for another release.
    /// </summary>
    async Task HandleAlbumMismatchAsync(TransmissionTorrent torrent, Album album, CancellationToken ct)
    {
        logger.LogWarning(
            "Not importing \"{Name}\": its files do not match \"{Album}\" by {Artist} (different release)",
            torrent.Name, album.Title, album.ArtistName);

        BlacklistRelease(album.Id, torrent.Name);
        await MarkGrabFailedAsync(torrent, ct);
        await transmission.SetTorrentLabels(torrent.Id, [MismatchLabel], ct);

        if (ranker is not null)
            await ranker.ResolveOutcomeAsync(album.Id, torrent.Name, "mismatch", ct);

        await library.RequeueAcquireAsync(album.ArtistName, album.Title, album.Id, deezerTrackId: 0,
            requireArtist: false, "the downloaded release was a different album", ct);

        if (notifications is not null)
            await notifications.AlbumDownloadFailedAsync(album, torrent.Name,
                "was a different album", ct);
    }

    /// <summary>Flips a torrent's grab to <c>Failed</c> so it is no longer shown as downloading.</summary>
    async Task MarkGrabFailedAsync(TransmissionTorrent torrent, CancellationToken ct)
    {
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            string hash = (torrent.HashString ?? "").Trim().ToLowerInvariant();
            GrabRecord? record = await db.Set<GrabRecord>()
                .FirstOrDefaultAsync(g => g.Source == GrabSource.Torrent && g.Identifier == hash, ct);
            if (record is null)
                return;
            record.State = GrabState.Failed;
            record.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not mark the grab failed for torrent {Hash}", torrent.HashString);
        }
    }

    /// <summary>
    /// Writes artist/album/title/track tags into the placed files (using the album's real track list
    /// matched by track number). Navidrome then groups and plays the album correctly even when the
    /// download had no usable tags.
    /// </summary>
    async Task<List<(int TrackId, string FilePath)>> TagPlacedFilesAsync(string folder, Album album, string? artistOverride = null)
    {
        List<(int TrackId, string FilePath)> placedFiles = [];
        try
        {
            // A torrent can be a compilation (e.g. "Off The Wall + Thriller"): tag only the folder that
            // actually is this album, never the sibling albums (which otherwise get mis-tagged).
            string tagRoot = ChooseAlbumFolder(folder, album.Title);
            List<string> files = [.. Directory.EnumerateFiles(tagRoot, "*", SearchOption.AllDirectories)
                .Where(IsAudioFile)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)];
            if (files.Count == 0)
                return placedFiles;

            List<Track> ordered = [.. album.Tracks
                .OrderBy(t => t.MediumNumber).ThenBy(t => t.AbsoluteTrackNumber).ThenBy(t => t.Id)];
            bool multiDisc = ordered.Select(t => t.MediumNumber).Distinct().Count() > 1;
            bool anyDiscMarker = files.Any(f => ParseMedium(Path.GetRelativePath(tagRoot, f)) > 1);

            string year = album.ReleaseDate?.Year is > 0 and < 3000 ? album.ReleaseDate!.Value.Year.ToString() : "";
            int total = album.Tracks.Count > 0 ? album.Tracks.Count : files.Count;
            HashSet<int> used = [];
            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];
                string relative = Path.GetRelativePath(tagRoot, file);
                int medium = ParseMedium(relative);
                int number = ParseTrackNumber(Path.GetFileNameWithoutExtension(file));
                if (number <= 0)
                    number = i + 1;

                // Soulseek files (and many rips) name the track rather than numbering it reliably, so
                // match on the title first; fall back to disc+track, then the release's running order.
                Track? track = MatchTrackByTitle(ordered, used, Path.GetFileNameWithoutExtension(file));
                if (track is null && (!multiDisc || anyDiscMarker))
                {
                    track = ordered.FirstOrDefault(t => t.Id != 0 && !used.Contains(t.Id) && t.MediumNumber == medium && t.TrackNumber == number)
                        ?? ordered.FirstOrDefault(t => t.Id != 0 && !used.Contains(t.Id) && t.MediumNumber == medium && t.AbsoluteTrackNumber == number)
                        ?? ordered.FirstOrDefault(t => t.Id != 0 && !used.Contains(t.Id) && t.AbsoluteTrackNumber == number)
                        ?? ordered.FirstOrDefault(t => t.Id != 0 && !used.Contains(t.Id) && t.TrackNumber == number);
                }
                track ??= ordered.ElementAtOrDefault(i);

                if (track is not null)
                {
                    used.Add(track.Id);
                    if (track.Id > 0)
                        placedFiles.Add((track.Id, Path.GetRelativePath(LibraryRoot, file)));
                }

                string title = !string.IsNullOrWhiteSpace(track?.Title)
                    ? track!.Title
                    : CleanTitle(Path.GetFileNameWithoutExtension(file));
                int trackNumber = track is { AbsoluteTrackNumber: > 0 } ? track.AbsoluteTrackNumber : number;

                await TagFileAsync(file, new TrackTag(
                    artistOverride ?? album.ArtistName, album.ArtistName, album.Title, title, trackNumber,
                    track?.MediumNumber ?? ParseMedium(relative), total, year,
                    track?.DeezerId ?? 0, album.DeezerId));
            }
            logger.LogInformation("Tagged {Count} file(s) for \"{Album}\"", files.Count, album.Title);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Tagging failed for {Folder}", folder);
        }
        return placedFiles;
    }

    /// <summary>The metadata written into a placed file. The Deezer ids are written too, so the file
    /// can be re-identified without the database.</summary>
    readonly record struct TrackTag(
        string Artist,
        string AlbumArtist,
        string Album,
        string Title,
        int Track,
        int Disc,
        int Total,
        string Year,
        long DeezerTrackId,
        long DeezerAlbumId);

    static async Task TagFileAsync(string path, TrackTag tag)
    {
        string dir = Path.GetDirectoryName(path)!;
        string ext = Path.GetExtension(path);
        string tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".arrk-tag" + ext);
        try
        {
            ProcessStartInfo psi = new("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };
            void Meta(string value) { psi.ArgumentList.Add("-metadata"); psi.ArgumentList.Add(value); }

            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-nostdin");
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            psi.ArgumentList.Add("-map"); psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("copy");
            Meta($"artist={tag.Artist}");
            Meta($"album_artist={tag.AlbumArtist}");
            Meta($"album={tag.Album}");
            Meta($"title={tag.Title}");
            Meta($"track={tag.Track}/{tag.Total}");
            if (tag.Disc > 0)
                Meta($"disc={tag.Disc}");
            if (!string.IsNullOrEmpty(tag.Year))
                Meta($"date={tag.Year}");
            if (tag.DeezerTrackId > 0)
                Meta($"ARRK_DEEZER_TRACKID={tag.DeezerTrackId}");
            if (tag.DeezerAlbumId > 0)
                Meta($"ARRK_DEEZER_ALBUMID={tag.DeezerAlbumId}");
            psi.ArgumentList.Add(tmp);

            using Process proc = Process.Start(psi)!;

            // Drain both pipes while ffmpeg runs: with them redirected but unread it blocks once a
            // pipe buffer fills, which can hang a large or numbered file indefinitely.
            Task<string> stderr = proc.StandardError.ReadToEndAsync();
            Task<string> stdout = proc.StandardOutput.ReadToEndAsync();

            using (CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2)))
            {
                try
                {
                    await proc.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }
            }
            await Task.WhenAll(stderr, stdout);

            if (proc.ExitCode == 0 && File.Exists(tmp))
                File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Tagging is best-effort; leave the original file in place.
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>
    /// Matches a file to an unused album track by title (for un-numbered / Soulseek names). When a
    /// probed length is supplied it breaks a tie between sibling tracks that share a cleaned title
    /// (e.g. an album's "Song" and "Song (End Title)") in favour of the closest-length track.
    /// </summary>
    internal static Track? MatchTrackByTitle(
        IReadOnlyList<Track> ordered, HashSet<int> used, string fileName, double preferDurationSeconds = 0)
    {
        // Strip a leading track number / disc-track marker ("01 ", "1-05 ") before tokenising.
        string cleaned = Regex.Replace(fileName, @"^\s*\d{1,2}\s*-\s*\d{1,3}[\s._-]+", "");
        cleaned = Regex.Replace(cleaned, @"^\s*\d{1,3}[\s._-]+", "");
        HashSet<string> fileTokens = Tokens(cleaned);
        HashSet<string> rawTokens = Tokens(fileName);
        if (fileTokens.Count == 0 && rawTokens.Count == 0)
            return null;

        Track? match = null;
        int best = 0;
        double bestDelta = double.MaxValue;
        foreach (Track track in ordered)
        {
            if (track.Id == 0 || used.Contains(track.Id) || string.IsNullOrWhiteSpace(track.Title))
                continue;

            // Compare like with like: the library title may carry Deezer's disambiguation tail
            // ("Let It Go (From "Frozen"/Soundtrack Version)") that the file name never has.
            HashSet<string> titleTokens = [.. Tokens(MusicText.CleanTrackTitle(track.Title)).Where(t => t.Length >= 2 && !NoiseTokens.Contains(t))];
            if (titleTokens.Count == 0)
                continue;

            int hits = titleTokens.Count(t => fileTokens.Contains(t) || rawTokens.Contains(t));
            // Require the whole title to be present so a short track can't claim a longer one.
            if (hits == 0 || hits < titleTokens.Count || hits < best)
                continue;

            // Same cleaned title, different recording: only the file's own length tells them apart.
            double delta = preferDurationSeconds > 0 && track.DurationSeconds > 0
                ? Math.Abs(track.DurationSeconds - preferDurationSeconds)
                : double.MaxValue;
            if (hits > best || delta < bestDelta)
            {
                best = hits;
                bestDelta = delta;
                match = track;
            }
        }
        return match;
    }

    static int ParseTrackNumber(string name)
    {
        // "1-01 - Title" (disc-track) → the track number (second group), not the disc.
        Match discTrack = Regex.Match(name, @"^\s*(\d{1,2})\s*-\s*(\d{1,2})(?:\D|$)");
        if (discTrack.Success && int.TryParse(discTrack.Groups[2].Value, out int track) && track > 0)
            return track;

        Match match = Regex.Match(name, @"\d+");
        return match.Success && int.TryParse(match.Value, out int number) ? number : 0;
    }

    /// <summary>
    /// True when the downloaded files plausibly are the identified album, judged by whether their file
    /// names share any distinctive word with the album's track titles. A release whose files carry real
    /// titles that share nothing with the album is a different release (e.g. a "Halo 3" request that
    /// actually downloaded "Halo 3: ODST"), so it must never be placed positionally under the requested
    /// album. Number-only rips ("01.flac") carry no title to judge and are always accepted, since
    /// positional placement is the only option there.
    /// </summary>
    internal static bool ReleaseMatchesAlbum(Album album, IReadOnlyList<string> audio)
    {
        if (audio.Count < 2)
            return true;

        HashSet<string> albumTokens = [];
        foreach (Track track in album.Tracks)
        {
            foreach (string token in TitleTokens(track.Title))
                albumTokens.Add(token);
        }
        if (albumTokens.Count == 0)
            return true;

        int titled = 0;
        bool overlap = false;
        foreach (string file in audio)
        {
            HashSet<string> fileTokens = TitleTokens(Path.GetFileNameWithoutExtension(file));
            if (fileTokens.Count == 0)
                continue;
            titled++;
            if (fileTokens.Overlaps(albumTokens))
                overlap = true;
        }
        // Two or more titled files that share absolutely nothing with any track title is the
        // wrong-release signal; one coincidental overlap is enough to trust the placement.
        return titled < 2 || overlap;
    }

    /// <summary>The distinctive title words of a track/file name: strip a leading track/disc marker
    /// then drop pure numbers, noise words and short words, leaving the words that identify the song.</summary>
    static HashSet<string> TitleTokens(string name)
    {
        string cleaned = Regex.Replace(name, @"^\s*\d{1,2}\s*-\s*\d{1,3}[\s._-]+", "");
        cleaned = Regex.Replace(cleaned, @"^\s*\d{1,3}[\s._-]+", "");
        return [.. Tokens(cleaned).Where(t => t.Length >= 3 && !NoiseTokens.Contains(t) && !t.All(char.IsDigit))];
    }

    static int ParseMedium(string relativePath)
    {
        Match marker = Regex.Match(relativePath, @"(?:disc|cd|disk)\s*(\d+)", RegexOptions.IgnoreCase);
        if (marker.Success && int.TryParse(marker.Groups[1].Value, out int medium) && medium > 0)
            return medium;

        // Leading "D-NN" disc-track naming (e.g. "2-05 Title.mp3"). Only when disc > 1: a single-disc
        // rip that happens to start with "1-" is not a disc marker.
        string file = Path.GetFileName(relativePath);
        Match discTrack = Regex.Match(file, @"^\s*(\d{1,2})\s*-\s*(\d{1,2})(?:\D|$)");
        if (discTrack.Success && int.TryParse(discTrack.Groups[1].Value, out int disc) && disc > 1)
            return disc;

        return 1;
    }

    static string CleanTitle(string name)
    {
        string[] parts = name.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 ? parts[^1] : name.Trim();
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true, EntryPoint = "link")]
    static extern int PosixLink(string oldPath, string newPath);

    /// <summary>
    /// Creates a hard link so the library and the seed share one copy of the data. Falls back to a
    /// copy when the platform/filesystem doesn't support links (e.g. cross-device, or non-Unix dev).
    /// </summary>
    static bool TryCreateHardLink(string source, string target)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;
        try
        {
            return PosixLink(source, target) == 0;
        }
        catch
        {
            return false;
        }
    }

    static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".alac", ".wma", ".ape", ".mpc", ".dsf", ".dff"];

    static bool IsAudioFile(string path) =>
        AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    static string SafeFolderName(string name)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
            return "Unknown Release";
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(trimmed.Select(c => invalid.Contains(c) ? '_' : c).ToArray());

        // "." and ".." are valid file names but not valid relative path segments: combining them
        // with the library root would escape it. Strip leading/trailing dots as well so names such
        // as "..." cannot collapse into a parent reference.
        safe = safe.Trim().Trim('.').Trim();
        if (safe.Length == 0 || safe is "." or "..")
            return "Unknown Release";

        return safe.Length > 120 ? safe[..120] : safe;
    }

    /// <summary>
    /// Deezer ids that currently have an in-flight Arrk grab (state <c>Grabbed</c>): the track ids
    /// and album ids we requested. Discovery uses these to show "Downloading" on an item it would
    /// otherwise render as merely Missing, without a second round trip to Transmission/slskd.
    /// </summary>
    public async Task<(HashSet<long> Tracks, HashSet<long> Albums)> GetActiveGrabDeezerIdsAsync(CancellationToken ct = default)
    {
        HashSet<long> tracks = [];
        HashSet<long> albums = [];
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            List<GrabRecord> grabs = await db.Set<GrabRecord>().AsNoTracking()
                .Where(g => g.State == GrabState.Grabbed && (g.DeezerTrackId > 0 || g.DeezerAlbumId > 0))
                .ToListAsync(ct);
            foreach (GrabRecord grab in grabs)
            {
                if (grab.DeezerTrackId > 0)
                    tracks.Add(grab.DeezerTrackId);
                if (grab.DeezerAlbumId > 0)
                    albums.Add(grab.DeezerAlbumId);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read active grab Deezer ids");
        }
        return (tracks, albums);
    }

    /// <summary>
    /// Maps Arrk album id → the state of Arrk's grab/download for it, joining Transmission
    /// torrents to the recorded grabs (so no per-album torrent label is needed). Lets the library UI
    /// show real download progress and mark albums Arrk placed as downloaded.
    /// </summary>
    public async Task<Dictionary<int, AlbumGrabState>> GetAlbumGrabStatesAsync(CancellationToken ct = default)
    {
        Dictionary<int, AlbumGrabState> states = [];

        List<TransmissionTorrent> torrents = [];
        try
        {
            torrents = await transmission.GetTorrents(ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read torrents for grab states");
        }

        List<GrabRecord> records = [];
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            records = await db.Set<GrabRecord>().AsNoTracking()
                .Where(g => g.AlbumId > 0 && g.State != GrabState.Failed)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read grab records");
        }

        Dictionary<string, GrabRecord> torrentRecords = records
            .Where(g => g.Source == GrabSource.Torrent && !string.IsNullOrEmpty(g.Identifier))
            .GroupBy(g => g.Identifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (TransmissionTorrent torrent in torrents)
        {
            if (!torrentRecords.TryGetValue(torrent.HashString, out GrabRecord? record))
                continue;

            bool placed = string.Equals(record.State, GrabState.Imported, StringComparison.OrdinalIgnoreCase)
                || torrent.Labels?.Contains(PlacedLabel, StringComparer.OrdinalIgnoreCase) == true;
            states[record.AlbumId] = new AlbumGrabState(
                AlbumId: record.AlbumId,
                Name: torrent.Name,
                Progress: torrent.PercentDone,
                Placed: placed,
                Size: torrent.TotalSize,
                Remaining: Math.Max(0, torrent.LeftUntilDone));
        }

        // Soulseek grabs: progress comes from slskd's transfers, unless a torrent already owns the album.
        List<SlskdDownload> soulseekDownloads = [];
        if (slskd.IsEnabled)
        {
            try
            {
                soulseekDownloads = await slskd.GetDownloadsAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read Soulseek transfers for grab states");
            }
        }

        foreach (IGrouping<int, GrabRecord> group in records
            .Where(g => g.Source == SoulseekSource)
            .GroupBy(g => g.AlbumId))
        {
            if (states.ContainsKey(group.Key))
                continue;

            bool imported = group.Any(g => string.Equals(g.State, GrabState.Imported, StringComparison.OrdinalIgnoreCase));
            string name = group.Select(g => g.ReleaseTitle)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
            (long size, long transferred) = SumSoulseekProgress(
                [.. group.Select(g => g.Identifier).Where(i => !string.IsNullOrEmpty(i))], soulseekDownloads);

            states[group.Key] = new AlbumGrabState(
                AlbumId: group.Key,
                Name: name,
                Progress: size > 0 ? Math.Clamp(transferred / (double)size, 0, 1) : (imported ? 1 : 0),
                Placed: imported,
                Size: size,
                Remaining: Math.Max(0, size - transferred));
        }

        return states;
    }

    /// <summary>Aggregates size/transferred for a set of Soulseek grab keys from slskd's transfers.</summary>
    static (long Size, long Transferred) SumSoulseekProgress(
        IReadOnlyCollection<string> keys, IReadOnlyCollection<SlskdDownload> downloads)
    {
        if (keys.Count == 0 || downloads.Count == 0)
            return (0, 0);

        HashSet<string> wanted = new(keys, StringComparer.OrdinalIgnoreCase);
        long size = 0;
        long transferred = 0;
        foreach (SlskdDownload download in downloads)
        {
            if (!wanted.Contains(SoulseekGrabKey(download.Filename))
                && !wanted.Contains(SoulseekFileKey(download.Filename)))
                continue;
            size += download.Size;
            transferred += download.BytesTransferred;
        }
        return (size, transferred);
    }

    /// <summary>
    /// Per-requested-track grab state, keyed by the Deezer track id the grab was recorded for, the
    /// same link the importer binds the finished file with. Lets the library songs grid and the
    /// album/playlist track lists show a song's own download progress without querying Transmission
    /// or slskd per row.
    /// </summary>
    public async Task<Dictionary<long, TrackGrabState>> GetTrackGrabStatesAsync(CancellationToken ct = default)
    {
        Dictionary<long, TrackGrabState> states = [];

        List<GrabRecord> records;
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            records = await db.Set<GrabRecord>().AsNoTracking()
                .Where(g => g.DeezerTrackId > 0 && g.State != GrabState.Failed)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read track grab records");
            return states;
        }
        if (records.Count == 0)
            return states;

        List<TransmissionTorrent> torrents = [];
        try
        {
            torrents = await transmission.GetTorrents(ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read torrents for track grab states");
        }

        Dictionary<string, TransmissionTorrent> byHash = torrents
            .Where(t => !string.IsNullOrEmpty(t.HashString))
            .GroupBy(t => t.HashString, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (GrabRecord record in records.Where(r => r.Source == GrabSource.Torrent))
        {
            if (!byHash.TryGetValue(record.Identifier, out TransmissionTorrent? torrent))
                continue;
            bool placed = string.Equals(record.State, GrabState.Imported, StringComparison.OrdinalIgnoreCase)
                || torrent.Labels?.Contains(PlacedLabel, StringComparer.OrdinalIgnoreCase) == true;
            states[record.DeezerTrackId] = new TrackGrabState(
                record.DeezerTrackId, torrent.Name, torrent.PercentDone, placed, torrent.TotalSize, Math.Max(0, torrent.LeftUntilDone));
        }

        List<SlskdDownload> soulseekDownloads = [];
        if (slskd.IsEnabled)
        {
            try
            {
                soulseekDownloads = await slskd.GetDownloadsAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read Soulseek transfers for track grab states");
            }
        }

        foreach (IGrouping<long, GrabRecord> group in records
            .Where(r => r.Source == SoulseekSource)
            .GroupBy(r => r.DeezerTrackId))
        {
            if (states.ContainsKey(group.Key))
                continue;

            bool imported = group.Any(g => string.Equals(g.State, GrabState.Imported, StringComparison.OrdinalIgnoreCase));
            string name = group.Select(g => g.ReleaseTitle)
                .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
            (long size, long transferred) = SumSoulseekProgress(
                [.. group.Select(g => g.Identifier).Where(i => !string.IsNullOrEmpty(i))], soulseekDownloads);

            states[group.Key] = new TrackGrabState(
                group.Key,
                name,
                size > 0 ? Math.Clamp(transferred / (double)size, 0, 1) : (imported ? 1 : 0),
                imported,
                size,
                Math.Max(0, size - transferred));
        }

        return states;
    }

    static string ResolveFolder(TransmissionTorrent torrent)
    {
        string parent = string.IsNullOrWhiteSpace(torrent.DownloadDir) ? MusicDownloadPath : torrent.DownloadDir;
        // The named path may be a folder or a single file; never fall back to the shared parent (that
        // would copy the whole downloads root, as happened for single-file grabs).
        return Path.Combine(parent, torrent.Name);
    }

    // Media info (codec/quality)

    readonly ConcurrentDictionary<int, MusicTechnical> technicalCache = new();

    /// <summary>
    /// Audio technical info for an album, probed from a placed file with ffprobe (lossless flag,
    /// codec, sample rate, bit depth). Drives the "Lossless" badge and the album's Media Info popup.
    /// </summary>
    public async Task<MusicTechnical> GetAlbumTechnicalAsync(int albumId, CancellationToken ct = default)
    {
        if (albumId <= 0)
            return new MusicTechnical();
        if (technicalCache.TryGetValue(albumId, out MusicTechnical? cached))
            return cached;

        MusicTechnical technical = new();
        try
        {
            Album? album = await library.GetAlbumAsync(albumId, ct);
            string? file = album is null ? null : FindFirstAlbumFile(album.Title);
            if (file is not null)
                technical = await ProbeAsync(file);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not probe technical info for album {AlbumId}", albumId);
        }

        if (technicalCache.Count > 500)
            technicalCache.Clear();
        technicalCache[albumId] = technical;
        return technical;
    }

    /// <summary>Invalidates the cached technical info (called after an import).</summary>
    public void InvalidateTechnicalCache()
    {
        technicalCache.Clear();
        trackTechnicalCache.Clear();
    }

    readonly ConcurrentDictionary<int, MusicTechnical> trackTechnicalCache = new();

    /// <summary>Audio technical info for a single placed track, probed from its file (per-track
    /// Media Info + the per-track Lossless badge).</summary>
    public async Task<MusicTechnical> GetTrackTechnicalAsync(int albumId, int trackId, CancellationToken ct = default)
    {
        if (trackId <= 0)
            return new MusicTechnical();
        if (trackTechnicalCache.TryGetValue(trackId, out MusicTechnical? cached))
            return cached;

        MusicTechnical technical = new();
        try
        {
            Album? album = await library.GetAlbumAsync(albumId, ct);
            Track? track = album?.Tracks.FirstOrDefault(t => t.Id == trackId);
            string? file = album is null || track is null ? null : ResolveTrackFile(album, track);
            if (file is not null)
                technical = await ProbeAsync(file);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not probe technical info for track {TrackId}", trackId);
        }

        if (trackTechnicalCache.Count > 2000)
            trackTechnicalCache.Clear();
        trackTechnicalCache[trackId] = technical;
        return technical;
    }

    /// <summary>Locates a track's placed file: the recorded path first, else a title match in the
    /// album's folder (covers tracks placed before we started recording file paths).</summary>
    string? ResolveTrackFile(Album album, Track track)
    {
        if (!string.IsNullOrEmpty(track.FilePath))
        {
            string full = Path.Combine(LibraryRoot, track.FilePath);
            if (File.Exists(full))
                return full;
        }
        if (string.IsNullOrWhiteSpace(album.Title) || !Directory.Exists(LibraryRoot))
            return null;

        string want = MusicText.NormalizeKey(track.Title);
        if (want.Length == 0)
            return null;
        string albumKey = MusicText.NormalizeKey(album.Title);
        foreach (string dir in Directory.EnumerateDirectories(LibraryRoot))
        {
            string dirKey = MusicText.NormalizeKey(Path.GetFileName(dir));
            if (dirKey.Length == 0 || albumKey.Length == 0)
                continue;
            if (!dirKey.Contains(albumKey, StringComparison.Ordinal) && !albumKey.Contains(dirKey, StringComparison.Ordinal))
                continue;
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsAudioFile))
            {
                string name = MusicText.NormalizeKey(Path.GetFileNameWithoutExtension(file));
                if (name.Length > 0 && (name == want || name.Contains(want, StringComparison.Ordinal) || want.Contains(name, StringComparison.Ordinal)))
                    return file;
            }
        }
        return null;
    }

    string? FindFirstAlbumFile(string albumTitle)
    {
        if (string.IsNullOrWhiteSpace(albumTitle) || !Directory.Exists(LibraryRoot))
            return null;

        string want = MusicText.NormalizeKey(albumTitle);
        foreach (string dir in Directory.EnumerateDirectories(LibraryRoot))
        {
            string name = MusicText.NormalizeKey(Path.GetFileName(dir));
            if (name.Length == 0 || want.Length == 0)
                continue;
            if (name.Contains(want, StringComparison.Ordinal) || want.Contains(name, StringComparison.Ordinal))
            {
                string? file = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).FirstOrDefault(IsAudioFile);
                if (file is not null)
                    return file;
            }
        }
        return null;
    }

    /// <summary>Whether a probed duration is close enough to the expected Deezer track length.
    /// Tolerance is the smaller of 10 % or 10 s, so a second-or-two deviation passes but a
    /// cover/remix/other song (which runs tens of seconds off) does not.</summary>
    internal static bool DurationsClose(double actualSeconds, int expectedSeconds)
    {
        double tolerance = Math.Min(10, expectedSeconds * 0.10);
        return Math.Abs(actualSeconds - expectedSeconds) <= tolerance;
    }

    /// <summary>The probed file's own tags, for a rejection reason (",  file tags: artist "X", album "Y"").</summary>
    internal static string DescribeTags(MusicTechnical probe)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(probe.TagArtist)) parts.Add($"artist \"{probe.TagArtist}\"");
        if (!string.IsNullOrWhiteSpace(probe.TagAlbum)) parts.Add($"album \"{probe.TagAlbum}\"");
        if (!string.IsNullOrWhiteSpace(probe.TagTitle)) parts.Add($"title \"{probe.TagTitle}\"");
        if (!string.IsNullOrWhiteSpace(probe.TagGenre)) parts.Add($"genre \"{probe.TagGenre}\"");
        return parts.Count == 0 ? "" : $" — file tags: {string.Join(", ", parts)}";
    }

    static async Task<MusicTechnical> ProbeAsync(string path)
    {
        ProcessStartInfo psi = new("ffprobe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("a:0");
        psi.ArgumentList.Add("-show_entries");
        psi.ArgumentList.Add("stream=codec_name,profile,bit_rate,sample_rate,bits_per_raw_sample,bits_per_sample,channels,channel_layout:format=bit_rate,size,duration,format_name,format_long_name:format_tags=artist,album_artist,album,title,track,disc,date,genre");
        psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("default=noprint_wrappers=1");
        psi.ArgumentList.Add(path);

        using Process proc = Process.Start(psi)!;
        Task<string> stderr = proc.StandardError.ReadToEndAsync();
        string output = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        await stderr;

        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int idx = line.IndexOf('=');
            if (idx > 0)
                map[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }

        string codec = map.GetValueOrDefault("codec_name", "");
        string profile = map.GetValueOrDefault("profile", "");
        string formatName = map.GetValueOrDefault("format_name", "");
        string formatLong = map.GetValueOrDefault("format_long_name", "");
        bool lossless = IsLosslessCodec(codec);
        int bitrateKbps = int.TryParse(map.GetValueOrDefault("bit_rate", ""), out int br) && br > 0 ? br / 1000 : 0;
        int sampleRate = int.TryParse(map.GetValueOrDefault("sample_rate", ""), out int sr) ? sr : 0;
        int bits = int.TryParse(map.GetValueOrDefault("bits_per_raw_sample", ""), out int bp) && bp > 0
            ? bp
            : (int.TryParse(map.GetValueOrDefault("bits_per_sample", ""), out int bps) ? bps : 0);
        int channels = int.TryParse(map.GetValueOrDefault("channels", ""), out int ch) ? ch : 0;
        double duration = double.TryParse(map.GetValueOrDefault("duration", ""),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double dur)
            ? dur : 0;
        long size = long.TryParse(map.GetValueOrDefault("size", ""), out long sz) ? sz : 0;
        string Tag(string name) => map.GetValueOrDefault("TAG:" + name, "");

        return new MusicTechnical
        {
            Codec = codec.ToUpperInvariant(),
            Profile = profile,
            Container = FriendlyContainer(formatName, formatLong),
            Quality = lossless ? "Lossless" : bitrateKbps > 0 ? "Lossy" : "",
            BitRate = bitrateKbps > 0 ? $"{bitrateKbps} kbps" : "",
            SampleRate = sampleRate > 0 ? $"{sampleRate / 1000.0:0.#} kHz" : "",
            Bits = bits > 0 ? $"{bits}-bit" : "",
            Channels = channels,
            ChannelLayout = map.GetValueOrDefault("channel_layout", ""),
            IsLossless = lossless,
            Spatial = DetectSpatial(codec, profile),
            DurationSeconds = duration,
            FileSizeBytes = size,
            TagTitle = Tag("title"),
            TagArtist = Tag("artist"),
            TagAlbumArtist = Tag("album_artist"),
            TagAlbum = Tag("album"),
            TagTrack = Tag("track"),
            TagDisc = Tag("disc"),
            TagDate = Tag("date"),
            TagGenre = Tag("genre")
        };
    }

    /// <summary>A short, friendly container name from ffprobe's format identifiers.</summary>
    static string FriendlyContainer(string formatName, string formatLong)
    {
        string f = formatName.ToLowerInvariant();
        if (f.Contains("flac")) return "FLAC";
        if (f.Contains("mp3")) return "MP3";
        if (f.Contains("mp4") || f.Contains("m4a") || f.Contains("mov")) return "M4A";
        if (f.Contains("ogg")) return formatLong.Contains("opus", StringComparison.OrdinalIgnoreCase) ? "Opus" : "OGG";
        if (f.Contains("wav")) return "WAV";
        if (f.Contains("aiff")) return "AIFF";
        if (f.Contains("ape")) return "APE";
        if (f.Contains("wv")) return "WavPack";
        return string.IsNullOrWhiteSpace(formatName) ? "" : formatName.ToUpperInvariant();
    }

    /// <summary>Immersive-audio badge from the stream's profile (e.g. E-AC-3 JOC → Dolby Atmos).</summary>
    static string DetectSpatial(string codec, string profile)
    {
        if (profile.Contains("atmos", StringComparison.OrdinalIgnoreCase))
            return "Dolby Atmos";
        if (codec.Equals("dts", StringComparison.OrdinalIgnoreCase) && profile.StartsWith("DTS:X", StringComparison.OrdinalIgnoreCase))
            return "DTS:X";
        return "";
    }

    static bool IsLosslessCodec(string codec) =>
        codec is "flac" or "alac" or "wav" or "ape" or "wavpack" or "tta" or "tak" or "truehd" or "mlp" or "dts"
        || codec.Contains("pcm", StringComparison.OrdinalIgnoreCase);


    // Scoring

    static MusicRelease ToRelease(ProwlarrSearchResult result, string artist, string target, bool requireArtist)
    {
        string guid = result.Guid ?? "";

        bool lossless = LooksLossless(result.Title);
        HashSet<string> releaseTokens = Tokens(result.Title);
        HashSet<string> targetTokens = Tokens(target);
        HashSet<string> expanded = Expand(releaseTokens);

        // A target token counts as matched when it (or its symbol expansion, e.g. ÷→divide) appears in
        // the release's expanded token set.
        int matched = targetTokens.Count(t => expanded.Contains(t) || expanded.Contains(ExpandSymbol(t)));
        double coverage = targetTokens.Count == 0 ? 0 : matched / (double)targetTokens.Count;

        // "Strong" tokens (words of 4+ letters, or numbers like the "3" in "Halo 3") must all be
        // present, so an unrelated release that merely shares one word can never be a match.
        bool missingStrong = targetTokens.Any(t => IsStrongToken(t)
            && !expanded.Contains(t) && !expanded.Contains(ExpandSymbol(t)));

        // The target's leading strong pair must appear adjacently in the release, so a release that
        // scatters the same words ("… Soundtrack 3 Angel Halo …") can't masquerade as the album.
        List<string> releaseOrdered = OrderedTokens(result.Title);
        List<string> targetStrongOrdered = [.. OrderedTokens(target).Where(IsStrongToken)];
        if (targetStrongOrdered.Count >= 2 && !HasAdjacentPair(releaseOrdered, targetStrongOrdered[0], targetStrongOrdered[1]))
            missingStrong = true;

        // A distinctive word appended straight after the matched title looks like a companion release:
        // "Halo 3 ODST" is not "Halo 3". It is a preference, not a veto, a release name is only a
        // hint, and hard-rejecting it starves an album whose only torrent carries a bare edition word
        // ("Halo 3 20th Anniversary Edition"). A companion is scored down so the real album wins when
        // both exist, but it is still returned when it is the only candidate; the importer's file/track
        // check (ReleaseMatchesAlbum) is what actually refuses a release that is a different album.
        HashSet<string> targetForms = Expand(targetTokens);
        foreach (string targetToken in targetTokens)
            targetForms.Add(ExpandSymbol(targetToken));
        bool companionTitle = HasExtraTitleWord(result.Title, releaseOrdered, targetForms, artist);

        int extras = releaseTokens
            .Where(t => !NoiseTokens.Contains(t) && !targetTokens.Contains(t) && !IsYear(t) && !expanded.Contains(t))
            .Distinct()
            .Count();

        int score = (int)Math.Round(coverage * 100) - (extras * 5);

        // A matching artist credit matters a lot, especially for generic song titles.
        bool artistMatched = ArtistMatches(artist, releaseTokens);
        if (artistMatched)
            score += requireArtist ? 15 : 4;
        // Releases that lead with the artist ("Michael Jackson - …") are the real album; a tribute or
        // compilation that merely mentions the artist later should not outrank them.
        if (ArtistAtStart(artist, result.Title))
            score += 8;
        if (lossless)
            score += 3;

        // Only a real magnet is a usable one-click grab. Some indexers (e.g. 1337x) report a tracker
        // page as the guid and expose only a Prowlarr download URL that Transmission can't fetch
        // (Cloudflare). Prefer releases that carry a magnet so we grab something that actually works.
        string magnet = guid.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            ? guid
            : (result.MagnetUrl.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? result.MagnetUrl : "");
        bool hasMagnet = magnet.Length > 0;
        if (hasMagnet)
            score += 10;

        // The bonus sum above can push a release past the 100 cap, so the companion penalty is applied
        // *after* clamping, otherwise a companion would saturate at 100 and tie the real album.
        int clamped = Math.Clamp(score, 0, 100);
        if (companionTitle)
            clamped -= CompanionTitlePenalty;

        string lower = result.Title.ToLowerInvariant();
        bool junk = JunkTokens.Any(j => lower.Contains(j)) ||
                    (lower.Contains("remix") && !target.Contains("remix", StringComparison.OrdinalIgnoreCase)) ||
                    (lower.Contains(" live ") && !target.Contains("live", StringComparison.OrdinalIgnoreCase)) ||
                    IsMonoRelease(result.Title, target, artist);

        return new MusicRelease
        {
            Title = result.Title,
            Indexer = string.IsNullOrWhiteSpace(result.IndexerName) ? result.Indexer : result.IndexerName,
            Size = result.Size,
            Seeders = result.Seeders,
            MagnetUrl = magnet,
            DownloadUrl = result.DownloadUrl,
            InfoHash = result.InfoHash,
            Protocol = string.IsNullOrWhiteSpace(result.Protocol) ? "torrent" : result.Protocol,
            PublishDate = result.PublishDate ?? DateTime.MinValue,
            IsLossless = lossless,
            HasMagnet = hasMagnet,
            MissingStrongToken = missingStrong,
            Score = Math.Clamp(clamped, 0, 100),
            Coverage = coverage,
            IsJunk = junk,
            ArtistMatched = artistMatched,
            MatchedTarget = target
        };
    }

    /// <summary>
    /// True when a release/transfer is flagged as mono audio (e.g. "(Mono)") while neither the title
    /// we asked for nor its artist is itself mono (so the band "Mono" is never filtered out). Shared
    /// by the torrent scorer and the Soulseek provider so the "no mono" rule lives in one place.
    /// </summary>
    public static bool IsMonoRelease(string releaseLabel, string target, string? artist = null)
    {
        HashSet<string> releaseTokens = Tokens(releaseLabel);
        if (!MonoTokens.Any(releaseTokens.Contains))
            return false;
        HashSet<string> targetTokens = Tokens(target);
        HashSet<string> artistTokens = Tokens(artist ?? "");
        return !MonoTokens.Any(targetTokens.Contains) && !MonoTokens.Any(artistTokens.Contains);
    }

    /// <summary>A token that must appear for a release to be a real match (a word, or a number).</summary>
    static bool IsStrongToken(string token) => token.Length >= 4 || token.All(char.IsDigit);

    /// <summary>Release/target tokens in their original order (for order-sensitive matching).</summary>
    static List<string> OrderedTokens(string value) =>
        [.. value.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0)
            .Select(w => w.ToLowerInvariant())];

    /// <summary>True when <paramref name="first"/> and <paramref name="second"/> appear consecutively.</summary>
    static bool HasAdjacentPair(List<string> tokens, string first, string second)
    {
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            if (TokenEquals(tokens[i], first) && TokenEquals(tokens[i + 1], second))
                return true;
        }
        return false;
    }

    static bool TokenEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ExpandSymbol(a), ExpandSymbol(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Expands a symbol token to the word indexers usually spell out (÷→divide, &→and, …).</summary>
    static string ExpandSymbol(string token) => token switch
    {
        "÷" => "divide",
        "×" => "x",
        "+" => "plus",
        "=" => "equals",
        _ => token
    };

    /// <summary>
    /// True when a release title carries a distinctive extra word straight after the matched target;
    /// the signal that it looks like a companion/different release rather than the one asked for. The
    /// word must be "strong" (four+ letters, or a number), bare (not inside brackets) and not a known
    /// edition, format, year or artist word; a release group, "(Deluxe Edition)", "[FLAC 24-48]" and
    /// "2017" are therefore all fine. "Halo 3 ODST" for "Halo 3" is not. This only <em>deprioritises</em>
    /// a release (see <see cref="CompanionTitlePenalty"/>); the importer's file/track check is what
    /// refuses a different album.
    /// </summary>
    static bool HasExtraTitleWord(string releaseTitle, List<string> orderedRelease, HashSet<string> targetForms, string artist)
    {
        int lastMatch = -1;
        for (int i = 0; i < orderedRelease.Count; i++)
        {
            if (targetForms.Contains(orderedRelease[i]))
                lastMatch = i;
        }
        if (lastMatch < 0 || lastMatch + 1 >= orderedRelease.Count)
            return false;

        string next = orderedRelease[lastMatch + 1];
        if (!IsStrongToken(next) || targetForms.Contains(next))
            return false;
        if (NoiseTokens.Contains(next) || TitleSafeTokens.Contains(next) || VersionJunkTokens.Contains(next))
            return false;
        if (IsYear(next) || ArtistStopwords.Contains(next))
            return false;
        if (ArtistMatches(artist, [next]))
            return false;
        return !BracketedTokens(releaseTitle).Contains(next);
    }

    /// <summary>The tokens that appear inside parentheses/brackets/braces of a release title (i.e. a
    /// release group or edition marker, never the bare title itself).</summary>
    static HashSet<string> BracketedTokens(string releaseTitle)
    {
        HashSet<string> tokens = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match group in Regex.Matches(releaseTitle, @"[\(\[\{]([^\)\]\}]*)[\)\]\}]"))
        {
            foreach (string token in Tokens(group.Groups[1].Value))
                tokens.Add(token);
        }
        return tokens;
    }

    static readonly HashSet<string> ArtistStopwords = new(StringComparer.OrdinalIgnoreCase)
    { "the", "and", "of", "a", "an", "feat", "ft", "with", "vs", "x" };

    /// <summary>
    /// True when the release title credits the artist, at least one distinctive artist token
    /// (length ≥ 3, not a stopword) appears in the release title.
    /// </summary>
    static bool ArtistMatches(string artist, HashSet<string> releaseTokens)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return false;

        HashSet<string> artistTokens = [.. Tokens(artist).Where(t => t.Length >= 3 && !ArtistStopwords.Contains(t))];
        if (artistTokens.Count == 0)
            artistTokens = Tokens(artist);
        return artistTokens.Count > 0 && artistTokens.Overlaps(releaseTokens);
    }

    /// <summary>True when the release title leads with the artist credit ("Artist - Album …").</summary>
    static bool ArtistAtStart(string artist, string releaseTitle)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(releaseTitle))
            return false;
        string wanted = MusicText.NormalizeKey(artist);
        return wanted.Length >= 3 && MusicText.NormalizeKey(releaseTitle).StartsWith(wanted, StringComparison.Ordinal);
    }

    static bool IsYear(string token) => token.Length == 4 && token.All(char.IsDigit);

    static bool LooksLossless(string title)
    {
        string t = title.ToLowerInvariant();
        return t.Contains("flac") || t.Contains("alac") || t.Contains("24bit") || t.Contains("24-") ||
               t.Contains("24/") || t.Contains("lossless") || t.Contains("wav") || t.Contains(" dsd");
    }

    static HashSet<string> Expand(HashSet<string> tokens)
    {
        HashSet<string> expanded = new(tokens, StringComparer.OrdinalIgnoreCase);
        if (tokens.Contains("ost"))
        {
            expanded.Add("original");
            expanded.Add("soundtrack");
        }
        if (tokens.Contains("soundtrack"))
            expanded.Add("ost");
        if (tokens.Contains("ep"))
            expanded.Add("extended");
        return expanded;
    }

    static HashSet<string> Tags(string value) => Tokens(value);

    static HashSet<string> Tokens(string value) =>
        value.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 0)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Picks the folder inside a (possibly multi-album) release that is the requested album, so a
    /// compilation rip doesn't get its sibling albums mis-tagged. Returns the root when the release is
    /// a single album.
    /// </summary>
    public static string ChooseAlbumFolder(string root, string albumTitle)
    {
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(root);
        }
        catch
        {
            return root;
        }
        if (dirs.Length < 2)
            return root;

        string want = MusicText.NormalizeKey(albumTitle);
        if (want.Length == 0)
            return root;

        HashSet<string> wantTokens = Tokens(albumTitle);
        int Score(string dir)
        {
            string name = MusicText.NormalizeKey(Path.GetFileName(dir));
            if (name.Length == 0)
                return 0;
            if (name.Contains(want, StringComparison.Ordinal))
                return 1000 + want.Length;
            if (want.Contains(name, StringComparison.Ordinal))
                return 500 + name.Length;
            return Tokens(Path.GetFileName(dir)).Count(wantTokens.Contains) * 10;
        }

        string best = dirs.OrderByDescending(Score).First();
        return Score(best) >= 20 ? best : root;
    }

    // Soulseek (slskd)

    // The artist/title/Deezer track id we searched for, keyed by the remote file's base name, so a
    // completed Soulseek download can be bound to the exact requested song without another round trip
    // (best-effort; the durable mapping is the GrabRecord).
    readonly ConcurrentDictionary<string, (string Artist, string Title, long DeezerTrackId)> soulseekIntents = new(StringComparer.OrdinalIgnoreCase);

    async Task<MusicGrabResult> TrySoulseekAsync(string artist, string title, int? albumId, long deezerTrackId, bool isTrack, CancellationToken ct)
    {
        // Deezer track titles carry a disambiguation tail ("Let It Go (From \"Frozen\"/Soundtrack
        // Version)") that no Soulseek filename has; search and match on the clean title instead.
        string searchTitle = MusicText.CleanTrackTitle(title);
        try
        {
            // Soulseek matches by filename, and a multi-word "artist title" query usually returns 0 on
            // the AND, so search the title first, then fall back to artist+title.
            SlskdSearch? search = await slskd.SearchAsync(searchTitle, SoulseekSearchSeconds, ct);
            if (search is null || search.Responses.Count == 0)
            {
                SlskdSearch? withArtist = await slskd.SearchAsync($"{artist} {searchTitle}".Trim(), SoulseekSearchSeconds, ct);
                if (withArtist is { Responses.Count: > 0 })
                    search = withArtist;
            }
            if (search is null)
            {
                logger.LogDebug("Soulseek search for \"{Title}\" by {Artist} returned nothing", searchTitle, artist);
                return new MusicGrabResult(false, null, "No Soulseek results.");
            }
            if (search.Responses.Count == 0)
            {
                logger.LogDebug("Soulseek search for \"{Title}\" by {Artist} returned 0 response(s)", searchTitle, artist);
                return new MusicGrabResult(false, null, "No Soulseek results.");
            }
            logger.LogDebug("Soulseek search for \"{Title}\" by {Artist} returned {Count} response(s)",
                searchTitle, artist, search.Responses.Count);

            HashSet<string> artistTokens = [.. Tokens(artist).Where(t => t.Length >= 3 && !ArtistStopwords.Contains(t))];
            if (artistTokens.Count == 0)
                artistTokens = Tokens(artist);

            // Sources that already failed for this request are skipped while scoring, otherwise the
            // single best candidate being blacklisted aborted the whole search even when other good
            // peers were right there, so a song never progressed.
            HashSet<string> blacklisted = await GetSoulseekBlacklistAsync(albumId ?? 0, deezerTrackId, ct);

            SoulseekCandidate? best = null;
            List<SoulseekCandidate> candidates = [];
            int matched = 0;
            int blocked = 0;
            foreach (SlskdResponse response in search.Responses)
            {
                foreach (SlskdFile file in response.Files)
                {
                    string path = file.Filename.Replace('\\', '/');
                    if (!IsAudioFile(path) || IsVersionJunkPath(path, title))
                        continue;
                    if (!SoulseekPathMatches(path, searchTitle, artistTokens))
                        continue;
                    if (blacklisted.Contains(SoulseekFileKey(path)))
                    {
                        blocked++;
                        continue;
                    }
                    matched++;

                    int bitrate = file.BitRate ?? 0;
                    bool lossless = bitrate == 0 || bitrate >= 1000 || LooksLossless(path);
                    int score = (lossless ? 1000 : bitrate)
                        + (response.HasFreeUploadSlot ? 25 : 0)
                        - Math.Min(response.QueueLength, 50);
                    SoulseekCandidate candidate = new(response.Username, file, score, lossless, bitrate,
                        response.HasFreeUploadSlot, response.QueueLength);
                    candidates.Add(candidate);
                    if (best is null || candidate.Score > best.Score)
                        best = candidate;
                }
            }

            if (best is null)
            {
                // Logged at Information so "why isn't this song downloading" is answerable: the search
                // had results, but none passed the artist/version filters or all were blacklisted.
                logger.LogInformation(
                    "No usable Soulseek source for \"{Title}\" by {Artist}: {Responses} response(s), {Blocked} blacklisted, {Matched} matched",
                    searchTitle, artist, search.Responses.Count, blocked, matched);
                return new MusicGrabResult(false, null, $"No matching Soulseek files for \"{searchTitle}\".");
            }

            if (isTrack && ranker is not null && candidates.Count > 1)
            {
                List<SoulseekCandidate> ordered = candidates
                    .OrderByDescending(candidate => candidate.Score)
                    .ToList();

                List<MusicTrackCandidate> rankCandidates = ordered
                    .Select(candidate => new MusicTrackCandidate(
                        candidate.Username,
                        candidate.File.Filename,
                        candidate.File.Size,
                        candidate.BitRate,
                        candidate.File.SampleRate,
                        candidate.File.BitDepth,
                        candidate.File.Length,
                        candidate.HasFreeUploadSlot,
                        candidate.QueueLength,
                        candidate.Score,
                        candidate.IsLossless))
                    .ToList();

                MusicTrackCandidate? aiPick = await ranker.PickTrackAsync(
                    albumId ?? 0, artist, title, deezerTrackId, rankCandidates, ct);

                if (aiPick is not null)
                {
                    SoulseekCandidate? selected = ordered.FirstOrDefault(candidate =>
                        string.Equals(candidate.Username, aiPick.Peer, StringComparison.Ordinal)
                        && string.Equals(candidate.File.Filename, aiPick.FileName, StringComparison.Ordinal));

                    if (selected is not null)
                        best = selected;
                }
            }

            MusicGrabResult queued = await EnqueueSoulseekAsync(
                artist, searchTitle, albumId, best.Username, searchTitle, [best.File], deezerTrackId, ct);
            if (queued.Success)
            {
                logger.LogInformation("Queued \"{Title}\" from Soulseek peer {Peer} ({Quality})",
                    searchTitle, best.Username, best.IsLossless ? "lossless" : $"{best.BitRate} kbps");
            }
            return queued;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // cancellation (e.g. a per-item timeout) must not be reported as a normal miss
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Soulseek search failed for {Title}", searchTitle);
            return new MusicGrabResult(false, null, $"Soulseek search failed: {ex.Message}");
        }
    }

    /// <summary>
    /// How long a single Soulseek search waits. Productive searches settle in a couple of seconds,
    /// so this only bounds the unproductive ones.
    /// </summary>
    const int SoulseekSearchSeconds = 15;

    /// <summary>
    /// True when a Soulseek file path plausibly holds the requested track. Soulseek filenames are
    /// user-chosen, so requiring every word of the title is too strict: a title is rarely stored with
    /// Deezer's disambiguation tail. Accept a file whose normalised file name contains the normalised
    /// title, or whose file name carries most of its words. The folder never supplies the title,
    /// because a peer's album folder is named after the album and would match every track in it. When
    /// the artist is known, its distinctive words must appear in the file name or folder, which keeps
    /// covers and karaoke out.
    /// </summary>
    static bool SoulseekPathMatches(string path, string title, HashSet<string> artistTokens)
    {
        string fileName = Path.GetFileName(path);
        string normTitle = MusicText.NormalizeKey(title);
        bool nameHit = normTitle.Length >= 3
            && MusicText.NormalizeKey(fileName).Contains(normTitle, StringComparison.Ordinal);

        if (!nameHit)
        {
            HashSet<string> fileTokens = Tokens(fileName);
            HashSet<string> titleTokens = [.. Tokens(title).Where(t => t.Length >= 2 && !NoiseTokens.Contains(t))];
            if (titleTokens.Count == 0)
                return false;
            int needed = Math.Max(1, (int)Math.Ceiling(titleTokens.Count * 0.6));
            if (titleTokens.Count(t => fileTokens.Contains(t)) < needed)
                return false;
        }

        return artistTokens.Count == 0 || artistTokens.Overlaps(Tokens(path));
    }

    /// <summary>True when a Soulseek path is a karaoke/tribute/etc. version rather than the real song.</summary>
    static bool IsJunkPath(string path) =>
        JunkTokens.Any(j => path.Contains(j, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a Soulseek path is a version the request didn't ask for (a remix/cover/live/instrumental
    /// rather than the original). Token-based, so "undercover" is not mistaken for a cover and a request
    /// that is itself a remix is not rejected.
    /// </summary>
    static bool IsVersionJunkPath(string path, string requestedTitle)
    {
        if (IsJunkPath(path))
            return true;
        HashSet<string> requested = Tokens(requestedTitle);
        return Tokens(path).Any(t => VersionJunkTokens.Contains(t) && !requested.Contains(t));
    }

    /// <summary>
    /// Places completed Soulseek downloads into the music library. A grab records the album it was
    /// for, so its files land in that album's folder, get tagged with the album's real track metadata
    /// and mark the album's tracks present, a Soulseek song/album then shows up exactly like a torrent
    /// import. Leftovers with no recorded grab (e.g. a manual slskd download) are matched to a library
    /// album by their folder name, and only then fall back to a best-effort Artist/Title placement.
    /// </summary>
    public async Task<int> ImportSoulseekAsync(CancellationToken ct = default)
    {
        if (!slskd.IsEnabled)
            return 0;

        string root = SoulseekRoot;
        if (!Directory.Exists(root))
            return 0;

        Dictionary<string, SoulseekGrab> grabs;
        try
        {
            grabs = await LoadSoulseekGrabsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load Soulseek grab records");
            grabs = [];
        }

        // Group by the grab key: a single requested song is its own group (keyed by folder + file),
        // an album grab keeps its folder grouping, and an unknown file falls back to its folder. The
        // file key stops a song downloaded into a generic folder ("music", "Disc 1") from colliding
        // with an unrelated grab in the same folder.
        Dictionary<string, List<string>> byFolder = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in EnumerateSoulseekDownloads(root))
        {
            if (ct.IsCancellationRequested)
                break;
            string relative = Path.GetRelativePath(root, file);
            string fileKey = SoulseekFileKey(relative);
            string folderKey = SoulseekGrabKey(relative);
            bool single = fileKey.Length > 0 && (soulseekIntents.ContainsKey(Path.GetFileName(file)) || grabs.ContainsKey(fileKey));
            string key = single ? fileKey : folderKey;
            if (key.Length == 0)
                continue;
            if (!byFolder.TryGetValue(key, out List<string>? files))
            {
                files = [];
                byFolder[key] = files;
            }
            files.Add(file);
        }

        int imported = 0;
        foreach ((string key, List<string> files) in byFolder)
        {
            if (ct.IsCancellationRequested)
                break;

            string folderName = Path.GetFileName(Path.GetDirectoryName(files[0]) ?? "");
            SoulseekGrab? grab = grabs.TryGetValue(key, out SoulseekGrab? found) ? found : null;
            int albumId = grab?.AlbumId ?? 0;
            string? trackArtist = null;

            // 1. Exact: the grab recorded the Deezer track id it was requested for. Resolve that track
            //    (title, performer, album) directly instead of guessing from the peer's folder or file
            //    name. This survives a restart and takes precedence over an album id resolved from the
            //    song title, which can belong to an unrelated release with the same name.
            if (files.Count == 1 && grab is { DeezerTrackId: > 0 })
            {
                (int exactAlbumId, string? exactArtist) = await EnsureDeezerTrackAlbumAsync(grab.DeezerTrackId, files, ct);
                if (exactAlbumId > 0)
                {
                    albumId = exactAlbumId;
                    trackArtist = exactArtist;
                }
            }

            // 2. Best-effort: the in-memory intent gives the requested artist and title until a
            //    restart. Peers use generic folders and unrelated compilations, so bind to the album
            //    that actually contains the song to keep the title and performer correct.
            if (albumId <= 0
                && files.Count == 1
                && soulseekIntents.TryGetValue(Path.GetFileName(files[0]), out (string Artist, string Title, long DeezerTrackId) intent)
                && !string.IsNullOrWhiteSpace(intent.Title))
            {
                (albumId, trackArtist) = await EnsureRequestedTrackAlbumAsync(intent.Artist, intent.Title, intent.DeezerTrackId, files, ct);
            }

            if (albumId <= 0 && !string.IsNullOrWhiteSpace(folderName))
            {
                // No recorded grab: try to resolve the album from the folder name, the same way a
                // torrent import recovers a grab whose record was lost.
                try
                {
                    Album? held = await library.FindAlbumByTorrentNameAsync(folderName, ct);
                    albumId = held?.Id ?? 0;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not resolve a library album for Soulseek folder {Folder}", folderName);
                }
            }

            // Resolve to a Deezer album (exact ids first, then folder/track search). Never invent an
            // unidentified album, if it isn't on Deezer we don't import it.
            if (albumId <= 0)
                albumId = await EnsureSoulseekAlbumAsync(folderName, key, files, ct);
            Album? album = albumId > 0 ? await library.GetAlbumAsync(albumId, ct) : null;
            if (album is null)
            {
                logger.LogInformation("Not importing Soulseek grab {Key}: not identified on Deezer", key);
                await CleanupSoulseekDownloadAsync(key, files, ct);
                continue;
            }

            using LiveActivityScope importing = liveActivities.Begin(
                LiveActivityKind.Import, $"Importing \"{album.Title}\"",
                string.IsNullOrWhiteSpace(album.ArtistName) ? "Adding to your library" : $"Adding {album.ArtistName} to your library");
            (List<(int TrackId, string FilePath)> placedTracks, string? rejectReason) =
                await PlaceAlbumAuditedAsync(album, files, trackArtist, move: true, ct, grab?.DeezerTrackId ?? 0);

            if (placedTracks.Count == 0 && files.Count == 1)
            {
                // The downloaded file was not the requested song (wrong release, track or version).
                // Reject it and blacklist the source so the song is re-acquired, rather than leaving the
                // request in flight or importing it under an unrelated track. Covers both id-keyed and
                // name-keyed single-song grabs.
                if (ranker is not null && grab?.DeezerTrackId > 0)
                    await ranker.ResolveTrackOutcomeAsync(grab.DeezerTrackId, files[0], "mismatch", ct);

                await RejectSoulseekSongAsync(key, rejectReason ?? "did not match the requested song", ct);
                await CleanupSoulseekDownloadAsync(key, files, ct);
                continue;
            }

            await library.MarkTracksPresentAsync(album.Id, placedTracks, ct);
            imported += placedTracks.Count;

            if (ranker is not null && grab?.DeezerTrackId > 0 && files.Count > 0)
                await ranker.ResolveTrackOutcomeAsync(grab.DeezerTrackId, files[0], "imported", ct);

            await MarkSoulseekImportedAsync(key, ct);
            if (placedTracks.Count > 0)
            {
                // The request is satisfied, drop it from the acquisition queue.
                await library.CompleteAcquireAsync(album.Id, grab?.DeezerTrackId ?? 0, ct);
            }

            if (placedTracks.Count > 0)
            {
                logger.LogInformation("Imported {Count} Soulseek file(s) into \"{Album}\" by {Artist}",
                    placedTracks.Count, album.Title, album.ArtistName);
                if (notifications is not null)
                    await notifications.ImportedAsync(album, album.Title, ResolvePlacedTracks(album, placedTracks), ct);
            }

            // Soulseek has no seeding obligation; once it's in the library drop the staged copy.
            await CleanupSoulseekDownloadAsync(key, files, ct);
        }

        if (imported > 0)
        {
            library.InvalidateAvailability();
            InvalidateTechnicalCache();
        }
        return imported;
    }

    /// <summary>Audio files slskd has completed, excluding its in-progress and shared folders.</summary>
    static IEnumerable<string> EnumerateSoulseekDownloads(string root)
    {
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string top = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (string.Equals(top, "incomplete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(top, "shares", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsAudioFile(file))
                yield return file;
        }
    }

    /// <summary>Soulseek grab targets keyed by the recorded file/folder key.</summary>
    async Task<Dictionary<string, SoulseekGrab>> LoadSoulseekGrabsAsync(CancellationToken ct)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<GrabRecord> records = await db.Set<GrabRecord>().AsNoTracking()
            .Where(g => g.Source == SoulseekSource && (g.AlbumId > 0 || g.DeezerTrackId > 0))
            .ToListAsync(ct);

        Dictionary<string, SoulseekGrab> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (GrabRecord record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Identifier))
                continue;
            map[record.Identifier] = new SoulseekGrab(record.AlbumId, record.ReleaseTitle, record.State, record.DeezerTrackId);
        }
        return map;
    }

    /// <summary>
    /// Places a Soulseek grab's files into its album folder, tags them and marks tracks present.
    /// Returns the number newly placed and whether the album resolved (so the caller can safely
    /// clean up the staged download once it is in the library, even if nothing was newly placed).
    /// </summary>
    async Task<(int Placed, bool Resolved)> PlaceSoulseekAlbumAsync(int albumId, string key, List<string> files, string? artistOverride, CancellationToken ct)
    {
        Album? album;
        try
        {
            album = await library.GetAlbumAsync(albumId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load album {AlbumId} for a Soulseek import", albumId);
            return (0, false);
        }
        if (album is null)
            return (0, false);

        string destName = string.IsNullOrWhiteSpace(album.ArtistName)
            ? SafeFolderName(album.Title)
            : $"{SafeFolderName(album.ArtistName)} - {SafeFolderName(album.Title)}";
        string dest = Path.Combine(LibraryRoot, destName);

        int placed = 0;
        foreach (string file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (PlaceFile(file, Path.Combine(dest, Path.GetFileName(file))))
                placed++;
        }

        if (placed > 0)
        {
            List<(int TrackId, string FilePath)> placedTracks = [];
            try
            {
                placedTracks = await TagPlacedFilesAsync(dest, album, artistOverride);
                if (placedTracks.Count > 0)
                    await library.MarkTracksPresentAsync(album.Id, placedTracks, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Tagging failed for Soulseek album {Album}", album.Title);
            }

            logger.LogInformation("Added {Count} Soulseek file(s) to album \"{Album}\"", placed, album.Title);
            if (notifications is not null)
                await notifications.ImportedAsync(album, album.Title, ResolvePlacedTracks(album, placedTracks), ct);
        }

        await MarkSoulseekImportedAsync(key, ct);
        return (placed, true);
    }

    /// <summary>
    /// Resolves (or creates) the library album a completed Soulseek folder belongs to, matched to
    /// Deezer when possible, and binds the grab key to it, so the download lands in
    /// Songs/Artists/Albums rather than only Navidrome. Returns the album id (0 when unresolvable).
    /// </summary>
    async Task<int> EnsureSoulseekAlbumAsync(string folderName, string key, List<string> files, CancellationToken ct)
    {
        (string artist, string album) = ParseAlbumFolder(folderName);
        if (string.IsNullOrWhiteSpace(album))
            album = string.IsNullOrWhiteSpace(folderName) ? "Unknown Album" : folderName;

        Album? resolved = await EnsureAlbumForDownloadAsync(artist, album, files, ct);
        if (resolved is null)
            return 0;

        await AssignSoulseekGrabAlbumAsync(key, resolved.Id, ct);
        return resolved.Id;
    }

    /// <summary>
    /// Resolves a completed song by the exact Deezer track id it was requested with: fetch the track,
    /// bind the album that contains it, and return the performer for the file's <c>artist</c> tag.
    /// Returns (0, null) when the id is unknown to Deezer so the caller can fall back.
    /// </summary>
    async Task<(int AlbumId, string? Artist)> EnsureDeezerTrackAlbumAsync(
        long deezerTrackId, IReadOnlyList<string> files, CancellationToken ct)
    {
        try
        {
            MusicItem? track = await deezer.GetTrackAsync(deezerTrackId, ct);
            if (track is { AlbumDeezerId: > 0 })
            {
                MusicItem? album = await deezer.GetAlbumAsync(track.AlbumDeezerId, ct);
                if (album is not null)
                {
                    // A single-song grab: the album exists only to hold this song, so it stays
                    // unmonitored and only the requested track is wanted (Partial/Unmonitored, not Missing).
                    Album? added = await library.AddOrUpdateAlbumAsync(
                        album, ct, monitored: false, monitoredTrackDeezerId: track.DeezerId);
                    if (added is { Tracks.Count: > 0 })
                        return (added.Id, track.ArtistName);

                    // Deezer returned the album but no track list: an empty album can't hold the song,
                    // so don't import under it (the caller cleans the grab up and the request retries).
                    logger.LogInformation(
                        "Deezer album {AlbumId} for track {TrackId} has no track list; not importing",
                        track.AlbumDeezerId, track.DeezerId);
                }
            }

            // The track id is known but Deezer wouldn't give us its album: don't import (the caller
            // skips it). Arrk only keeps music it can bind to a Deezer release.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve Deezer track id {TrackId}", deezerTrackId);
        }
        return (0, null);
    }

    /// <summary>
    /// Resolves the album for a single requested song. The song is the identity: prefer the exact
    /// Deezer track id, else search the track by artist + title, and bind to the album that track
    /// belongs to; the caller then tags the file with that track's real title and performer. Falls back
    /// to a local single when the song isn't on Deezer. Returns the album id and the performer to use
    /// for the file's <c>artist</c> tag.
    /// </summary>
    async Task<(int AlbumId, string? Artist)> EnsureRequestedTrackAlbumAsync(
        string artist, string title, long deezerTrackId, IReadOnlyList<string> files, CancellationToken ct)
    {
        MusicItem? track = deezerTrackId > 0
            ? await deezer.GetTrackAsync(deezerTrackId, ct)
            : null;
        track ??= await FindDeezerTrackAsync(artist, title, ct);
        if (track is { AlbumDeezerId: > 0 })
        {
            try
            {
                MusicItem? album = await deezer.GetAlbumAsync(track.AlbumDeezerId, ct);
                if (album is not null)
                {
                    // As above: a requested song doesn't make us want the whole album.
                    Album? added = await library.AddOrUpdateAlbumAsync(
                        album, ct, monitored: false, monitoredTrackDeezerId: track.DeezerId);
                    if (added is { Tracks.Count: > 0 })
                        return (added.Id, track.ArtistName);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not bind the requested track \"{Title}\" to its Deezer album", title);
            }
        }

        // Not on Deezer (or the lookup failed): Arrk only keeps identified music, so the caller
        // skips this download rather than creating an unidentified single.
        return (0, track?.ArtistName);
    }

    /// <summary>The best Deezer track for a requested artist+title (exact title, then artist, then any).</summary>
    async Task<MusicItem?> FindDeezerTrackAsync(string artist, string title, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        string artistKey = MusicText.NormalizeKey(MusicText.CleanReleaseName(artist));
        try
        {
            List<MusicItem> tracks = await SearchTrackCandidatesAsync(artist, title, ct);
            return tracks.FirstOrDefault(t => t.AlbumDeezerId > 0 && MusicText.TitleMatches(t.Title, title)
                        && (artistKey.Length == 0 || MusicText.NormalizeKey(t.ArtistName) == artistKey))
                ?? tracks.FirstOrDefault(t => t.AlbumDeezerId > 0 && MusicText.TitleMatches(t.Title, title))
                ?? tracks.FirstOrDefault(t => t.AlbumDeezerId > 0 && artistKey.Length > 0 && MusicText.NormalizeKey(t.ArtistName) == artistKey);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Deezer track lookup failed for \"{Title}\" by {Artist}", title, artist);
            return null;
        }
    }

    /// <summary>
    /// Resolves the library album a completed download belongs to, creating it when it isn't held
    /// yet. Prefers a Deezer match (so the album/tracks get real metadata and ids and count as
    /// identified); when the release simply isn't on Deezer it is created from the files themselves
    /// and reported as Unidentified rather than being dropped.
    /// </summary>
    public async Task<Album?> EnsureAlbumForDownloadAsync(string artist, string album, IReadOnlyList<string> files, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(album) && string.IsNullOrWhiteSpace(artist))
            return null;

        if (string.IsNullOrWhiteSpace(album))
            album = artist;

        // 1. Already held (raw title first, then a cleaned-up title so messy folder names still match).
        string cleanAlbum = MusicText.CleanReleaseName(album);
        try
        {
            foreach (string candidate in new[] { album, cleanAlbum }.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Album? existing = await library.FindAlbumByTitleAsync(artist, candidate, ct);
                if (existing is not null)
                    return existing;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not look up album \"{Album}\" by {Artist}", album, artist);
        }

        // 2. Match Deezer so the album is bound to real metadata/track ids.
        MusicItem? deezerAlbum = await FindDeezerAlbumAsync(artist, album, ct);
        if (deezerAlbum is { DeezerId: > 0 })
        {
            try
            {
                Album? added = await library.AddOrUpdateAlbumAsync(deezerAlbum, ct);
                if (added is not null)
                {
                    logger.LogInformation("Bound completed download to Deezer album \"{Album}\" by {Artist}", added.Title, added.ArtistName);
                    return added;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not add Deezer album \"{Album}\" to the library", album);
            }
        }

        // Not on Deezer: Arrk keeps only identified music, so the caller skips this download.
        logger.LogInformation("Download \"{Album}\" by {Artist} isn't on Deezer; not importing", album, artist);
        return null;
    }

    /// <summary>Best Deezer album match for an artist/title (cleaned title, exact → artist), with its
    /// track list. Falls back to a track search (for singles whose album name differs).</summary>
    async Task<MusicItem?> FindDeezerAlbumAsync(string artist, string album, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(album))
            return null;

        string cleanAlbum = MusicText.CleanReleaseName(album);
        if (string.IsNullOrWhiteSpace(cleanAlbum))
            cleanAlbum = album.Trim();
        string cleanArtist = MusicText.CleanReleaseName(artist);
        string titleKey = MusicText.NormalizeKey(cleanAlbum);
        string artistKey = MusicText.NormalizeKey(cleanArtist);

        try
        {
            foreach (string query in new[] { $"{cleanArtist} {cleanAlbum}".Trim(), cleanAlbum }
                .Where(q => !string.IsNullOrWhiteSpace(q)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<MusicItem> results = await deezer.SearchAlbumsAsync(query, 5, ct);
                MusicItem? match = results.FirstOrDefault(a => MusicText.TitleMatches(a.Title, cleanAlbum) && (artistKey.Length == 0 || MusicText.NormalizeKey(a.ArtistName) == artistKey))
                    ?? results.FirstOrDefault(a => MusicText.TitleMatches(a.Title, cleanAlbum))
                    ?? results.FirstOrDefault(a => artistKey.Length > 0 && MusicText.NormalizeKey(a.ArtistName) == artistKey);
                if (match is { DeezerId: > 0 })
                    return await deezer.GetAlbumAsync(match.DeezerId, ct) ?? match;
            }

            // Album search failed: a single/song whose title differs from its album, search tracks
            // and bind to the album the track belongs to.
            string trackQuery = string.IsNullOrWhiteSpace(cleanArtist) ? cleanAlbum : $"{cleanArtist} {cleanAlbum}";
            List<MusicItem> tracks = await deezer.SearchTracksAsync(trackQuery, 5, ct);
            MusicItem? track = tracks.FirstOrDefault(t => MusicText.NormalizeKey(t.Title) == titleKey && t.AlbumDeezerId > 0)
                ?? tracks.FirstOrDefault(t => t.AlbumDeezerId > 0 && (artistKey.Length == 0 || MusicText.NormalizeKey(t.ArtistName) == artistKey));
            if (track is { AlbumDeezerId: > 0 })
                return await deezer.GetAlbumAsync(track.AlbumDeezerId, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not match \"{Album}\" by {Artist} on Deezer", album, artist);
        }
        return null;
    }

    /// <summary>
    /// Best-effort identification of an album from its title and/or track titles. Tracks are searched
    /// and voted on FIRST (more specific, and immune to a junk folder "artist"), then an album search.
    /// </summary>
    public async Task<MusicItem?> IdentifyAlbumAsync(string artist, string albumTitle, IReadOnlyList<string> trackTitles, CancellationToken ct = default)
    {
        MusicItem? byTracks = await IdentifyAlbumByTracksAsync(artist, albumTitle, trackTitles, ct);
        if (byTracks is { DeezerId: > 0 })
            return byTracks;

        return await FindDeezerAlbumAsync(artist, albumTitle, ct);
    }

    /// <summary>Searches each track and votes for the album they belong to.</summary>
    async Task<MusicItem?> IdentifyAlbumByTracksAsync(string artist, string albumTitle, IReadOnlyList<string> trackTitles, CancellationToken ct)
    {
        List<string> titles = [.. trackTitles.Where(t => !string.IsNullOrWhiteSpace(t)).Take(10)];
        if (titles.Count == 0)
            return null;

        string artistKey = MusicText.NormalizeKey(MusicText.CleanReleaseName(artist));
        Dictionary<long, int> votes = [];
        foreach (string title in titles)
        {
            if (ct.IsCancellationRequested)
                break;
            try
            {
                List<MusicItem> tracks = await SearchTrackCandidatesAsync(artist, title, ct);
                MusicItem? best = tracks.FirstOrDefault(t => t.AlbumDeezerId > 0 && MusicText.TitleMatches(t.Title, title))
                    ?? (artistKey.Length > 0 ? tracks.FirstOrDefault(t => t.AlbumDeezerId > 0 && MusicText.NormalizeKey(t.ArtistName) == artistKey) : null);
                if (best is { AlbumDeezerId: > 0 })
                    votes[best.AlbumDeezerId] = votes.GetValueOrDefault(best.AlbumDeezerId) + 1;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Track identification search failed for \"{Title}\"", title);
            }
        }

        if (votes.Count == 0)
            return null;

        KeyValuePair<long, int> top = votes.OrderByDescending(v => v.Value).First();
        int needed = titles.Count <= 2 ? titles.Count : Math.Max(2, titles.Count / 2);
        if (top.Value < needed)
            return null;

        logger.LogInformation("Identified \"{Album}\" by {Artist} as Deezer album {Id} from {Votes}/{Total} track(s)",
            albumTitle, artist, top.Key, top.Value, titles.Count);
        return await deezer.GetAlbumAsync(top.Key, ct);
    }

    /// <summary>Track-search candidates for identification: the title alone first (Deezer's relevance
    /// ranks the original first, and a junk folder "artist" can't bias the query), then "artist title".</summary>
    async Task<List<MusicItem>> SearchTrackCandidatesAsync(string artist, string title, CancellationToken ct)
    {
        List<MusicItem> results = [];
        try
        {
            results.AddRange(await deezer.SearchTracksAsync(title.Trim(), 5, ct));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Deezer track search failed for \"{Title}\"", title);
        }
        if (!string.IsNullOrWhiteSpace(artist))
        {
            try
            {
                foreach (MusicItem candidate in await deezer.SearchTracksAsync($"{artist} {title}".Trim(), 5, ct))
                {
                    if (!results.Any(r => r.DeezerId == candidate.DeezerId))
                        results.Add(candidate);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Deezer track search failed for \"{Query}\"", $"{artist} {title}");
            }
        }
        return results;
    }

    /// <summary>Re-tags the placed files of an album (using the album's current track list), used
    /// after an album is re-identified so Navidrome groups the files under the correct release. Also
    /// resolves and records each file's path when it wasn't known (older imports).</summary>
    public async Task<int> RetagAlbumFilesAsync(int albumId, CancellationToken ct = default)
    {
        Album? album = await library.GetAlbumAsync(albumId, ct);
        if (album is null)
            return 0;

        string year = album.ReleaseDate?.Year is > 0 and < 3000 ? album.ReleaseDate.Value.Year.ToString() : "";
        int total = album.Tracks.Count > 0 ? album.Tracks.Count : 1;
        int tagged = 0;
        Dictionary<int, string> resolved = [];
        foreach (Track track in album.Tracks.Where(t => t.HasFile))
        {
            if (ct.IsCancellationRequested)
                break;

            string? full = null;
            if (!string.IsNullOrEmpty(track.FilePath))
            {
                string candidate = Path.Combine(LibraryRoot, track.FilePath);
                if (File.Exists(candidate))
                    full = candidate;
            }
            full ??= FindLibraryFileByTitle(track.Title);
            if (full is null)
                continue;

            try
            {
                int number = track.AbsoluteTrackNumber > 0 ? track.AbsoluteTrackNumber : track.TrackNumber;
                await TagFileAsync(full, new TrackTag(
                    album.ArtistName, album.ArtistName, album.Title, track.Title, number,
                    track.MediumNumber, total, year, track.DeezerId, album.DeezerId));
                tagged++;
                resolved[track.Id] = Path.GetRelativePath(LibraryRoot, full);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not re-tag {File}", full);
            }
        }

        if (resolved.Count > 0)
            await library.SetTrackFilePathsAsync(album.Id, resolved, ct);
        return tagged;
    }

    /// <summary>Locates a placed file anywhere in the library by its track title (for re-tagging older
    /// imports that didn't record a file path).</summary>
    string? FindLibraryFileByTitle(string title)
    {
        string want = MusicText.NormalizeKey(title);
        if (want.Length == 0 || !Directory.Exists(LibraryRoot))
            return null;
        foreach (string dir in Directory.EnumerateDirectories(LibraryRoot))
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsAudioFile))
            {
                string name = MusicText.NormalizeKey(Path.GetFileNameWithoutExtension(file));
                if (name.Length > 0 && (name == want || name.Contains(want, StringComparison.Ordinal)))
                    return file;
            }
        }
        return null;
    }

    /// <summary>Binds a Soulseek grab key to a resolved library album (creating the grab if needed).</summary>
    async Task AssignSoulseekGrabAlbumAsync(string key, int albumId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key) || albumId <= 0)
            return;
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            GrabRecord? record = await db.Set<GrabRecord>()
                .FirstOrDefaultAsync(g => g.Source == SoulseekSource && g.Identifier == key, ct);
            if (record is null)
            {
                db.Set<GrabRecord>().Add(new GrabRecord
                {
                    AlbumId = albumId,
                    Source = SoulseekSource,
                    Identifier = key,
                    State = GrabState.Grabbed,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            }
            else
            {
                record.AlbumId = albumId;
                record.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not bind Soulseek grab {Key} to album {AlbumId}", key, albumId);
        }
    }

    /// <summary>Splits "Artist - Album" folder names; falls back to an album-only name. A leading
    /// year/number (e.g. "1969 - Top Of The Pops") is not treated as an artist.</summary>
    static (string Artist, string Album) ParseAlbumFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return ("", "");

        string cleaned = folderName.Trim();
        int separator = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0)
        {
            string artist = cleaned[..separator].Trim();
            string album = cleaned[(separator + 3)..].Trim();
            if (artist.Any(char.IsLetter))
                return (artist, album);
        }
        return ("", cleaned);
    }

    /// <summary>
    /// Removes a completed Soulseek grab once imported: its slskd transfer records and the staged
    /// download copy. Soulseek has no seeding obligation for downloads (sharing is a separate
    /// concern), so leaving them only grows the completed list. The library copy is a hard link, so
    /// removing the download never removes the music.
    /// </summary>
    async Task CleanupSoulseekDownloadAsync(string key, IReadOnlyList<string> files, CancellationToken ct)
    {
        if (!slskd.RemoveCompletedAfterImport)
            return;

        try
        {
            List<SlskdDownload> downloads = await slskd.GetDownloadsAsync(ct);
            foreach (SlskdDownload download in downloads.Where(d => d.IsComplete
                && (SoulseekGrabKey(d.Filename) == key || SoulseekFileKey(d.Filename) == key)))
            {
                try { await slskd.CancelDownloadAsync(download.Username, download.Id, remove: true, ct); }
                catch (Exception ex) { logger.LogDebug(ex, "Could not remove Soulseek transfer {Id}", download.Id); }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list Soulseek transfers for cleanup");
        }

        // Delete the staged folder (only under our slskd downloads root, never shares/incomplete).
        try
        {
            string? dir = files.Count > 0 ? Path.GetDirectoryName(files[0]) : null;
            if (!string.IsNullOrEmpty(dir) && IsUnderSoulseekDownloads(dir) && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                logger.LogInformation("Removed imported Soulseek download folder {Folder}", dir);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not delete the imported Soulseek download folder");
        }
    }

    /// <summary>True when a path is inside the slskd downloads root (never its shares/incomplete dirs).</summary>
    bool IsUnderSoulseekDownloads(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(SoulseekRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;
        string relative = Path.GetRelativePath(root, full);
        string top = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return !string.Equals(top, "incomplete", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(top, "shares", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Marks a Soulseek grab as imported (idempotent).</summary>
    async Task MarkSoulseekImportedAsync(string key, CancellationToken ct)
    {
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            GrabRecord? record = await db.Set<GrabRecord>()
                .FirstOrDefaultAsync(g => g.Source == SoulseekSource && g.Identifier == key, ct);
            if (record is null || record.State == GrabState.Imported)
                return;
            record.State = GrabState.Imported;
            record.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not mark Soulseek grab {Key} imported", key);
        }
    }

    /// <summary>
    /// Detects Soulseek grabs that have failed or stalled (errored, or queued with no progress past
    /// the configured window), marks the source failed for the album (a persisted blacklist),
    /// cancels its transfers and re-acquires the album from another source. Returns how many albums
    /// were re-queued.
    /// </summary>
    public async Task<int> CheckSoulseekFailuresAsync(CancellationToken ct = default)
    {
        if (!slskd.IsEnabled)
            return 0;

        double timeoutMinutes = GetConfigDouble("services:slskd.failureTimeoutMinutes", 45);
        double queuedMinutes = GetConfigDouble("services:slskd.queuedTimeoutMinutes", 60);

        List<GrabRecord> grabs;
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            grabs = await db.Set<GrabRecord>().AsNoTracking()
                .Where(g => g.Source == SoulseekSource
                    && g.State == GrabState.Grabbed
                    && g.CreatedAt < DateTime.UtcNow.AddMinutes(-1))
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read Soulseek grabs for the failure check");
            return 0;
        }
        if (grabs.Count == 0)
            return 0;

        List<SlskdDownload> downloads;
        try
        {
            downloads = await slskd.GetDownloadsAsync(ct);
        }
        catch (Exception ex)
        {
            // slskd unreachable: don't blacklist everything, just wait for the next run.
            logger.LogDebug(ex, "Could not read Soulseek transfers for the failure check");
            return 0;
        }

        Dictionary<string, List<SlskdDownload>> byKey = new(StringComparer.OrdinalIgnoreCase);
        foreach (SlskdDownload download in downloads)
        {
            // Index by BOTH the folder key and the single-file key. An album grab is keyed by folder
            // and a single-song grab by file, so indexing only by folder left every song grab unmatched
            //, it looked like it had no transfer and only failed after the full (45 min) timeout, even
            // when slskd had already rejected the file ("File not shared"/"Too many megabytes").
            Index(byKey, SoulseekGrabKey(download.Filename), download);
            Index(byKey, SoulseekFileKey(download.Filename), download);
        }

        DateTime now = DateTime.UtcNow;
        int requeued = 0;
        foreach (GrabRecord grab in grabs)
        {
            if (ct.IsCancellationRequested)
                break;

            List<SlskdDownload> files = byKey.TryGetValue(grab.Identifier, out List<SlskdDownload>? matched) ? matched : [];
            if (files.Count > 0 && files.All(f => f.IsComplete))
                continue; // done — the importer will place it

            bool failed = files.Any(f => f.IsFailed);
            // A queued-but-never-started transfer gets the longer queue window; one that vanished or
            // started and made no progress gets the shorter failure window.
            bool allQueued = files.Count > 0 && files.All(f => f.IsQueued);
            DateTime enqueued = files.Count == 0 ? grab.CreatedAt : files.Min(f => f.EnqueuedAt ?? grab.CreatedAt);
            bool queuedTooLong = allQueued && now - enqueued > TimeSpan.FromMinutes(queuedMinutes);
            bool noTransfers = files.Count == 0 && now - grab.CreatedAt > TimeSpan.FromMinutes(timeoutMinutes);
            bool startedNoProgress = !allQueued
                && files.Count > 0
                && files.All(f => f.BytesTransferred <= 0)
                && now - grab.CreatedAt > TimeSpan.FromMinutes(timeoutMinutes);

            if (!failed && !queuedTooLong && !noTransfers && !startedNoProgress)
                continue;

            string reason = failed
                ? DescribeSoulseekFailure(files)
                : queuedTooLong
                    ? $"stalled — queued at the peer for over {queuedMinutes:0} min"
                    : $"stalled — no progress after {timeoutMinutes:0} min";
            await FailSoulseekGrabAsync(grab, files, reason, ct);
            requeued++;
        }

        return requeued;
    }

    /// <summary>Adds a download to the lookup under a key (ignoring empty keys and duplicates).</summary>
    static void Index(Dictionary<string, List<SlskdDownload>> map, string key, SlskdDownload download)
    {
        if (string.IsNullOrEmpty(key))
            return;
        if (!map.TryGetValue(key, out List<SlskdDownload>? list))
        {
            list = [];
            map[key] = list;
        }
        if (!list.Contains(download))
            list.Add(download);
    }

    /// <summary>A readable reason for a failed transfer, using slskd's own error text when present.</summary>
    internal static string DescribeSoulseekFailure(IReadOnlyList<SlskdDownload> files)
    {
        SlskdDownload? bad = files.FirstOrDefault(f => f.IsFailed);
        if (bad is null)
            return "failed";
        string detail = !string.IsNullOrWhiteSpace(bad.Exception) ? bad.Exception.Trim() : bad.State;
        return detail.Length > 0 ? $"failed — {detail}" : "failed";
    }

    /// <summary>Blacklists a Soulseek grab, cancels its transfers, notifies and re-acquires the album.</summary>
    async Task FailSoulseekGrabAsync(GrabRecord grab, IReadOnlyList<SlskdDownload> files, string reason, CancellationToken ct)
    {
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            GrabRecord? record = await db.Set<GrabRecord>().FirstOrDefaultAsync(g => g.Id == grab.Id, ct);
            if (record is not null && record.State != GrabState.Failed)
            {
                record.State = GrabState.Failed;
                record.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not mark Soulseek grab {Id} failed", grab.Id);
        }

        foreach (SlskdDownload file in files.Where(f => !f.IsComplete))
        {
            try
            {
                await slskd.CancelDownloadAsync(file.Username, file.Id, remove: true, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not cancel Soulseek transfer {Id}", file.Id);
            }
        }

        logger.LogWarning("Soulseek grab for album {AlbumId} (\"{Title}\") {Reason}; re-queued for another attempt",
            grab.AlbumId, grab.ReleaseTitle, reason);

        if (notifications is not null)
        {
            Album? album = null;
            try
            {
                album = await library.GetAlbumAsync(grab.AlbumId, ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not load album {AlbumId} for the failure notification", grab.AlbumId);
            }
            await notifications.AlbumDownloadFailedAsync(album, grab.ReleaseTitle, reason, ct);
        }

        try
        {
            // Re-join the queue; the drain job picks it up on its next run (a minute), sitting behind
            // the requests that have failed fewer times. Nothing runs immediately.
            (string artist, string title) = await ResolveGrabTrackIdentityAsync(grab, ct);
            await library.RequeueAcquireAsync(artist, title, grab.AlbumId, grab.DeezerTrackId, artist.Length > 0, reason, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not re-queue the Soulseek request for grab {Id}", grab.Id);
        }
    }

    /// <summary>
    /// The performer and title a song grab was actually for, resolved from its Deezer track id (the
    /// durable identity the importer binds the file with). Falls back to the recorded release title
    /// with no artist when the id is unknown or Deezer can't be reached.
    /// </summary>
    async Task<(string Artist, string Title)> ResolveGrabTrackIdentityAsync(GrabRecord grab, CancellationToken ct)
    {
        if (grab.DeezerTrackId > 0)
        {
            try
            {
                MusicItem? track = await deezer.GetTrackAsync(grab.DeezerTrackId, ct);
                if (track is not null && !string.IsNullOrWhiteSpace(track.ArtistName))
                    return (track.ArtistName, string.IsNullOrWhiteSpace(track.Title) ? grab.ReleaseTitle : track.Title);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not resolve the requested song for failed grab {Id}", grab.Id);
            }
        }
        return ("", grab.ReleaseTitle);
    }

    /// <summary>
    /// Rejects a completed single-file Soulseek grab that didn't turn out to be the requested song
    /// (e.g. the file's duration doesn't match the Deezer track): blacklist the source and re-acquire.
    /// </summary>
    async Task RejectSoulseekSongAsync(string key, string reason, CancellationToken ct)
    {
        GrabRecord? record = null;
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            record = await db.Set<GrabRecord>().AsNoTracking()
                .FirstOrDefaultAsync(g => g.Source == SoulseekSource && g.Identifier == key, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not load the rejected Soulseek grab {Key}", key);
        }

        if (record is not null)
            await FailSoulseekGrabAsync(record, [], reason, ct);
    }

    /// <summary>Reads a positive double from configuration, falling back when unset/invalid.</summary>
    double GetConfigDouble(string key, double fallback) =>
        double.TryParse(configuration[key], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) && value > 0
            ? value
            : fallback;

    /// <summary>Links (or copies) one file into the library, returning true when it was newly created.</summary>
    static bool PlaceFile(string source, string target)
    {
        if (File.Exists(target))
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!TryCreateHardLink(source, target))
            File.Copy(source, target, overwrite: false);
        return true;
    }

    sealed record SoulseekGrab(int AlbumId, string ReleaseTitle, string State, long DeezerTrackId);
    sealed record SoulseekCandidate(string Username, SlskdFile File, int Score, bool IsLossless, int BitRate, bool HasFreeUploadSlot, int QueueLength);
}

/// <summary>A candidate release for a music album/track, scored for relevance.</summary>
public record MusicRelease
{
    public string Title { get; init; } = "";
    public string Indexer { get; init; } = "";
    public long Size { get; init; }
    public int Seeders { get; init; }
    public string MagnetUrl { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string InfoHash { get; init; } = "";
    public string Protocol { get; init; } = "torrent";
    public DateTime PublishDate { get; init; }
    public bool IsLossless { get; init; }
    public bool HasMagnet { get; init; }
    /// <summary>True when the release is missing a required ("strong") target token, not a match.</summary>
    public bool MissingStrongToken { get; init; }
    public int Score { get; init; }
    public double Coverage { get; init; }
    public bool IsJunk { get; init; }
    public bool ArtistMatched { get; init; }
    public string MatchedTarget { get; init; } = "";

    /// <summary>True when the release is a plausible match (covers most of the target title).</summary>
    public bool IsMatch => Coverage >= 0.6 && !MissingStrongToken && !IsJunk;

    public string FormatLabel => IsLossless ? "Lossless" : "";

    public string SizeLabel => Size <= 0 ? ""
        : Size >= 1L << 30 ? $"{Size / (double)(1L << 30):0.0} GB"
        : $"{Size / (double)(1L << 20):0} MB";

    /// <summary>Human summary for the release picker, e.g. "FLAC · 120 MB · 75 seeders · TPB".</summary>
    public string Summary
    {
        get
        {
            List<string> parts = [];
            if (!string.IsNullOrEmpty(FormatLabel)) parts.Add(FormatLabel);
            if (!string.IsNullOrEmpty(SizeLabel)) parts.Add(SizeLabel);
            parts.Add($"{Seeders} seeder{(Seeders == 1 ? "" : "s")}");
            if (!string.IsNullOrEmpty(Indexer)) parts.Add(Indexer);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>The outcome of a search+grab or a single grab.</summary>
public record MusicGrabResult(bool Success, MusicRelease? Release, string Message, long TorrentId = 0);

/// <summary>Live state of an Arrk music grab for an album, derived from Transmission.</summary>
public record AlbumGrabState(int AlbumId, string Name, double Progress, bool Placed, long Size, long Remaining)
{
    public bool IsDownloading => !Placed && Progress < 1.0;
    public bool IsComplete => Progress >= 1.0;
    public int Percent => (int)Math.Round(Math.Clamp(Progress * 100, 0, 100));
}

/// <summary>Live state of an Arrk grab for one requested track (keyed by its Deezer track id).</summary>
public record TrackGrabState(long DeezerTrackId, string Name, double Progress, bool Placed, long Size, long Remaining)
{
    public bool IsDownloading => !Placed && Progress < 1.0;
    public bool IsComplete => Progress >= 1.0;
    public int Percent => (int)Math.Round(Math.Clamp(Progress * 100, 0, 100));
}
