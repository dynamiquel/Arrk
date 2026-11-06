using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Models;
using Arrk.Music.Models;
using Arrk.Navidrome;

namespace Arrk.Music.Services;

/// <summary>
/// Arrk's music library: artists, albums and tracks persisted in the app database, plus a
/// per-track availability view resolved against Navidrome. Arrk is the source of truth for
/// library intent; Navidrome handles playback.
/// </summary>
public partial class MusicLibrary(
    IDbContextFactory<ArrkDbContext> dbFactory,
    Microsoft.Extensions.Options.IOptionsMonitor<NavidromeConfig> navidromeConfig,
    NavidromeClient navidromeClient,
    ILogger<MusicLibrary> logger) : IMusicLibrary, IMusicLibraryWriter
{
    static readonly TimeSpan AvailabilityTtl = TimeSpan.FromSeconds(60);

    readonly Lock _gate = new();
    // Serialises album upserts so two concurrent imports can't create the same album twice.
    readonly SemaphoreSlim _writeGate = new(1, 1);
    DateTime _availabilityAt = DateTime.MinValue;
    Dictionary<string, int> _availabilityByAlbumArtist = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, int> _availabilityByAlbum = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Drops the cached Navidrome availability index (called after an import).</summary>
    public void InvalidateAvailability()
    {
        lock (_gate)
        {
            _availabilityAt = DateTime.MinValue;
        }
    }

    // Reads

    public async Task<List<Artist>> GetAllArtistsAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicArtist> artists = await db.Set<MusicArtist>()
            .Include(a => a.Albums).ThenInclude(a => a.Tracks)
            .AsNoTracking()
            .OrderBy(a => a.SortName)
            .ToListAsync(ct);

        Dictionary<string, int> byAlbumArtist = (await GetAvailabilityAsync(ct)).ByAlbumArtist;
        Dictionary<string, int> byAlbum = (await GetAvailabilityAsync(ct)).ByAlbum;
        return artists.Select(a => ToArtist(a, byAlbumArtist, byAlbum)).ToList();
    }

    public async Task<List<Album>> GetAllAlbumsAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicAlbum> albums = await db.Set<MusicAlbum>()
            .Include(a => a.Artist)
            .Include(a => a.Tracks)
            .AsNoTracking()
            .ToListAsync(ct);

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return albums
            .Select(a => ToAlbum(a, ResolveFileCount(a, byAlbumArtist, byAlbum)))
            .ToList();
    }

    /// <summary>
    /// Albums we hold that have files, newest first. This is the single source of truth for
    /// "Recently Added" (Navidrome is only the player, so its filesystem "newest" view is not used).
    /// </summary>
    public async Task<List<Album>> GetRecentlyAddedAlbumsAsync(int limit = 30, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicAlbum> albums = await db.Set<MusicAlbum>()
            .Include(a => a.Artist)
            .Include(a => a.Tracks)
            .AsNoTracking()
            .ToListAsync(ct);

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return albums
            .Select(a => ToAlbum(a, ResolveFileCount(a, byAlbumArtist, byAlbum)))
            .Where(a => a.TrackFileCount > 0)
            .OrderByDescending(a => a.Added)
            .ThenByDescending(a => a.ReleaseDate ?? DateTime.MinValue)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    public async Task<Album?> GetAlbumAsync(int albumId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAlbum? album = await db.Set<MusicAlbum>()
            .Include(a => a.Artist)
            .Include(a => a.Tracks)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == albumId, ct);
        if (album is null)
            return null;

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return ToAlbum(album, ResolveFileCount(album, byAlbumArtist, byAlbum));
    }

    public async Task<List<Album>> GetArtistAlbumsAsync(int artistId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicAlbum> albums = await db.Set<MusicAlbum>()
            .Include(a => a.Artist)
            .Include(a => a.Tracks)
            .Where(a => a.ArtistId == artistId)
            .AsNoTracking()
            .ToListAsync(ct);

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return albums.Select(a => ToAlbum(a, ResolveFileCount(a, byAlbumArtist, byAlbum))).ToList();
    }

    public async Task<List<Track>> GetTracksAsync(int albumId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAlbum? album = await db.Set<MusicAlbum>()
            .Include(a => a.Tracks)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == albumId, ct);
        if (album is null)
            return [];

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        int fileCount = ResolveFileCount(album, byAlbumArtist, byAlbum);
        bool complete = fileCount > 0 && fileCount >= Math.Max(album.TotalTrackCount, album.Tracks.Count);
        // Trust the placement flag recorded on import. Rows without one predate per-track placement,
        // so fall back to whether the album is complete.
        return album.Tracks
            .OrderBy(t => t.DiscNumber).ThenBy(t => t.AbsoluteTrackNumber).ThenBy(t => t.Id)
            .Select(t => ToTrack(t, t.HasFile || complete))
            .ToList();
    }

    public async Task<Artist?> GetArtistAsync(int artistId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicArtist? artist = await db.Set<MusicArtist>()
            .Include(a => a.Albums).ThenInclude(a => a.Tracks)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artistId, ct);
        if (artist is null)
            return null;

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return ToArtist(artist, byAlbumArtist, byAlbum);
    }

    public async Task<Artist?> FindArtistByDeezerIdAsync(long deezerId, CancellationToken ct = default)
    {
        if (deezerId <= 0)
            return null;
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicArtist? artist = await db.Set<MusicArtist>()
            .Include(a => a.Albums).ThenInclude(a => a.Tracks)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.DeezerId == deezerId, ct);
        if (artist is null)
            return null;

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return ToArtist(artist, byAlbumArtist, byAlbum);
    }

    public async Task<Artist?> FindArtistByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string key = Normalize(name);
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicArtist? artist = (await db.Set<MusicArtist>()
                .Include(a => a.Albums).ThenInclude(a => a.Tracks)
                .AsNoTracking()
                .ToListAsync(ct))
            .FirstOrDefault(a => Normalize(a.Name) == key);
        if (artist is null)
            return null;

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return ToArtist(artist, byAlbumArtist, byAlbum);
    }

    public async Task<Album?> FindAlbumByTitleAsync(string? artist, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        string titleKey = Normalize(title);
        string artistKey = Normalize(artist ?? "");
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicAlbum> albums = await db.Set<MusicAlbum>()
            .Include(a => a.Artist)
            .Include(a => a.Tracks)
            .AsNoTracking()
            .ToListAsync(ct);

        MusicAlbum? match =
            albums.FirstOrDefault(a => Normalize(a.Title) == titleKey && Normalize(a.Artist?.Name ?? "") == artistKey)
            ?? albums.FirstOrDefault(a => Normalize(a.Title) == titleKey);
        if (match is null)
            return null;

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return ToAlbum(match, ResolveFileCount(match, byAlbumArtist, byAlbum));
    }

    /// <summary>
    /// The library track matching a Deezer track id (or, failing that, a normalised title), with its
    /// album id. Lets a song opened from a playlist, which only carries its identity, be resolved to
    /// the library row whose file Research/Delete/Media info act on.
    /// </summary>
    public async Task<(int AlbumId, int TrackId)?> FindTrackAsync(
        long deezerTrackId, string title, CancellationToken ct = default)
    {
        if (deezerTrackId <= 0 && string.IsNullOrWhiteSpace(title))
            return null;

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicTrack? track = null;
        if (deezerTrackId > 0)
            track = await db.Set<MusicTrack>().AsNoTracking().FirstOrDefaultAsync(t => t.DeezerId == deezerTrackId, ct);

        if (track is null && !string.IsNullOrWhiteSpace(title))
        {
            string key = Normalize(title);
            List<MusicTrack> tracks = await db.Set<MusicTrack>().AsNoTracking().ToListAsync(ct);
            track = tracks.FirstOrDefault(t => Normalize(t.Title) == key);
        }

        return track is null ? null : (track.AlbumId, track.Id);
    }

    public async Task<Album?> FindAlbumByDeezerIdAsync(long deezerId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAlbum? album = await db.Set<MusicAlbum>()
            .Include(a => a.Artist)
            .Include(a => a.Tracks)
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.DeezerId == deezerId, ct);
        if (album is null)
            return null;

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return ToAlbum(album, ResolveFileCount(album, byAlbumArtist, byAlbum));
    }

    // Writes

    /// <summary>Adds or updates an artist (matched by Deezer id or name).</summary>
    public async Task<Artist> AddOrUpdateArtistAsync(MusicItem artist, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicArtist? entity = await FindArtistEntityAsync(db, artist, ct);
        if (entity is null)
        {
            entity = new MusicArtist { Added = DateTime.UtcNow };
            db.Set<MusicArtist>().Add(entity);
        }

        entity.Name = string.IsNullOrWhiteSpace(artist.Title) ? artist.ArtistName : artist.Title;
        entity.SortName = StripArticle(entity.Name);
        if (artist.DeezerId > 0)
            entity.DeezerId = artist.DeezerId;
        if (!string.IsNullOrWhiteSpace(artist.Overview))
            entity.Overview = artist.Overview;
        if (artist.Genres.Count > 0)
            entity.GenresJson = JsonSerializer.Serialize(artist.Genres);
        if (artist.Rating.HasValue)
            entity.Rating = artist.Rating;
        if (!string.IsNullOrWhiteSpace(artist.ArtUrl) && !artist.ArtUrl.StartsWith("/api/", StringComparison.Ordinal))
            entity.PosterUrl = artist.ArtUrl;
        if (!string.IsNullOrWhiteSpace(artist.BackdropUrl))
            entity.FanartUrl = artist.BackdropUrl;
        entity.Monitored = true;

        await db.SaveChangesAsync(ct);
        return (await GetArtistAsync(entity.Id, ct))!;
    }

    /// <summary>
    /// Adds or updates an album (matched by Deezer id or artist+title) and its tracks. The artist
    /// is created on demand.
    /// </summary>
    /// <param name="monitored">
    /// True for an album the user explicitly asked for (add/request), so all its tracks are wanted.
    /// False when the album only exists because we grabbed one of its songs (a playlist auto-download
    /// or a single-song grab): the album (and its unrequested tracks) stay unmonitored, so they read as
    /// Partial/Unmonitored rather than Missing. Monitoring is never turned <em>off</em> here, an album
    /// the user already added stays monitored when a later single-track import touches it.
    /// </param>
    /// <param name="monitoredTrackDeezerId">
    /// The one track this import was actually asked for (0 for a whole-album add). Only used when the
    /// album is otherwise unmonitored, so the requested song is still "Missing" until it lands.
    /// </param>
    public async Task<Album?> AddOrUpdateAlbumAsync(
        MusicItem album, CancellationToken ct = default, bool monitored = true, long monitoredTrackDeezerId = 0)
    {
        // Two concurrent import runs must not create the same album twice (the import backlog can
        // outlast the every-minute schedule), so album writes are serialised.
        await _writeGate.WaitAsync(ct);
        try
        {
            return await AddOrUpdateAlbumCoreAsync(album, monitored, monitoredTrackDeezerId, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    async Task<Album?> AddOrUpdateAlbumCoreAsync(
        MusicItem album, bool monitored, long monitoredTrackDeezerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(album.Title))
            return null;

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);

        string artistName = !string.IsNullOrWhiteSpace(album.ArtistName) ? album.ArtistName : album.Title;
        MusicArtist? artist = await FindArtistEntityAsync(db, new MusicItem
        {
            EntityType = MusicEntityType.Artist,
            Title = artistName,
            ArtistName = artistName,
            DeezerId = album.ArtistDeezerId
        }, ct);
        if (artist is null)
        {
            artist = new MusicArtist
            {
                Name = artistName,
                SortName = StripArticle(artistName),
                DeezerId = album.ArtistDeezerId,
                Added = DateTime.UtcNow
            };
            db.Set<MusicArtist>().Add(artist);
            await db.SaveChangesAsync(ct);
        }

        MusicAlbum? entity = await FindAlbumEntityAsync(db, artist.Id, album, ct);
        bool newAlbum = entity is null;
        if (entity is null)
        {
            // Never persist an album with no track list. A Deezer album detail whose track fetch
            // failed (or a summary with no tracks) would otherwise create a rows-less album that
            // every later single-song import resolves to and can never place into, the request then
            // loops forever. Returning null lets the caller retry and re-fetch the detail instead.
            if (album.Tracks.Count == 0)
                return null;

            entity = new MusicAlbum { ArtistId = artist.Id, Added = DateTime.UtcNow, Monitored = monitored };
            db.Set<MusicAlbum>().Add(entity);
        }
        else if (entity.Added == default)
        {
            entity.Added = DateTime.UtcNow;
        }

        entity.ArtistId = artist.Id;
        entity.Title = album.Title;
        if (!string.IsNullOrWhiteSpace(album.AlbumType))
            entity.AlbumType = album.AlbumType;
        if (album.Year > 0 && entity.ReleaseDate is null)
            entity.ReleaseDate = new DateTime(album.Year, 1, 1);
        if (album.DeezerId > 0)
            entity.DeezerId = album.DeezerId;
        if (!string.IsNullOrWhiteSpace(album.Overview))
            entity.Overview = album.Overview;
        if (!string.IsNullOrWhiteSpace(album.ArtUrl) && !album.ArtUrl.StartsWith("/api/", StringComparison.Ordinal))
            entity.PosterUrl = album.ArtUrl;
        if (album.TrackCount > 0)
            entity.TotalTrackCount = album.TrackCount;
        // Never turn monitoring off here: an album the user explicitly added must stay monitored even
        // when a later single-track import touches it. A brand-new album is monitored only when the
        // caller asked for the whole release.
        if (!newAlbum)
            entity.Monitored = entity.Monitored || monitored;
        bool albumMonitored = entity.Monitored;

        if (album.Tracks.Count > 0)
        {
            // Carry placement state over so re-adding an album (e.g. to backfill Deezer ids) never
            // loses the files we already placed, and drop exact duplicate entries in the source list.
            Dictionary<string, MusicTrack> existingByKey = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, MusicTrack> existingByTitle = new(StringComparer.OrdinalIgnoreCase);
            foreach (MusicTrack existing in entity.Tracks)
            {
                existingByKey.TryAdd(TrackKey(existing.DiscNumber, existing.TrackNumber, existing.Title), existing);
                existingByTitle.TryAdd(Normalize(existing.Title), existing);
            }

            List<MusicTrack> desired = [];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            int absolute = 1;
            foreach (MusicTrackItem t in album.Tracks)
            {
                int disc = t.DiscNumber is > 0 ? t.DiscNumber : 1;
                int number = t.Number > 0 ? t.Number : absolute;
                MusicTrack track = new()
                {
                    ArtistId = artist.Id,
                    Title = t.Title,
                    DiscNumber = disc,
                    TrackNumber = number,
                    AbsoluteTrackNumber = absolute,
                    DurationSeconds = t.DurationSeconds,
                    DeezerId = t.DeezerId,
                    // A whole-album add wants every track; a single-song import wants only that song.
                    Monitored = albumMonitored
                        || (monitoredTrackDeezerId > 0 && t.DeezerId == monitoredTrackDeezerId)
                };
                absolute++;
                if (!seen.Add(TrackKey(disc, number, t.Title)))
                    continue;

                MusicTrack? prior = existingByKey.GetValueOrDefault(TrackKey(disc, number, t.Title))
                    ?? existingByTitle.GetValueOrDefault(Normalize(t.Title));
                if (prior is not null)
                {
                    track.HasFile = prior.HasFile;
                    track.FilePath = prior.FilePath;
                    track.Monitored = track.Monitored || prior.Monitored;
                }
                desired.Add(track);
            }

            bool same = entity.Tracks.Count == desired.Count
                && entity.Tracks.OrderBy(t => t.DiscNumber).ThenBy(t => t.AbsoluteTrackNumber)
                    .Zip(desired, (a, b) => a.DiscNumber == b.DiscNumber && a.TrackNumber == b.TrackNumber
                        && a.Title == b.Title && a.DeezerId == b.DeezerId && a.Monitored == b.Monitored)
                    .All(x => x);
            if (!same)
            {
                entity.Tracks.Clear();
                foreach (MusicTrack track in desired)
                    entity.Tracks.Add(track);
            }
            entity.TotalTrackCount = desired.Count;
        }

        await db.SaveChangesAsync(ct);
        return await GetAlbumAsync(entity.Id, ct);
    }

    /// <summary>
    /// Re-identifies an existing (unidentified) album in place from a matched metadata album: sets
    /// its Deezer identity and rebuilds its track list, carrying over each track's placed
    /// file (matched by title). Used to "rescue" albums whose folder/title was junk.
    /// </summary>
    public async Task<Album?> UpgradeAlbumAsync(int albumId, MusicItem detail, CancellationToken ct = default)
    {
        if (detail.DeezerId <= 0 || detail.Tracks.Count == 0)
            return null;

        await _writeGate.WaitAsync(ct);
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            MusicAlbum? album = await db.Set<MusicAlbum>().Include(a => a.Tracks).FirstOrDefaultAsync(a => a.Id == albumId, ct);
            if (album is null)
                return null;
            // Already identified as this exact release: nothing to change (avoids re-tagging every run).
            if (album.DeezerId == detail.DeezerId && Normalize(album.Title) == Normalize(detail.Title))
                return await GetAlbumAsync(album.Id, ct);

            string artistName = !string.IsNullOrWhiteSpace(detail.ArtistName) ? detail.ArtistName : album.Title;
            MusicArtist? artist = await FindArtistEntityAsync(db, new MusicItem
            {
                EntityType = MusicEntityType.Artist,
                Title = artistName,
                ArtistName = artistName,
                DeezerId = detail.ArtistDeezerId
            }, ct);
            if (artist is null)
            {
                artist = new MusicArtist
                {
                    Name = artistName,
                    SortName = StripArticle(artistName),
                    DeezerId = detail.ArtistDeezerId,
                    Added = DateTime.UtcNow
                };
                db.Set<MusicArtist>().Add(artist);
                await db.SaveChangesAsync(ct);
            }

            album.ArtistId = artist.Id;
            album.Title = detail.Title;
            album.DeezerId = detail.DeezerId;
            if (detail.Year > 0 && album.ReleaseDate is null)
                album.ReleaseDate = new DateTime(detail.Year, 1, 1);
            if (!string.IsNullOrWhiteSpace(detail.AlbumType))
                album.AlbumType = detail.AlbumType;
            if (!string.IsNullOrWhiteSpace(detail.ArtUrl) && !detail.ArtUrl.StartsWith("/api/", StringComparison.Ordinal))
                album.PosterUrl = detail.ArtUrl;
            if (!string.IsNullOrWhiteSpace(detail.Overview))
                album.Overview = detail.Overview;

            Dictionary<string, MusicTrack> prior = new(StringComparer.OrdinalIgnoreCase);
            foreach (MusicTrack track in album.Tracks)
                prior.TryAdd(Normalize(track.Title), track);

            bool albumMonitored = album.Monitored;
            album.Tracks.Clear();
            int absolute = 1;
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (MusicTrackItem t in detail.Tracks)
            {
                int disc = t.DiscNumber is > 0 ? t.DiscNumber : 1;
                int number = t.Number > 0 ? t.Number : absolute;
                if (!seen.Add(TrackKey(disc, number, t.Title)))
                {
                    absolute++;
                    continue;
                }
                MusicTrack track = new()
                {
                    ArtistId = artist.Id,
                    Title = t.Title,
                    DiscNumber = disc,
                    TrackNumber = number,
                    AbsoluteTrackNumber = absolute,
                    DurationSeconds = t.DurationSeconds,
                    DeezerId = t.DeezerId,
                    Monitored = albumMonitored
                };
                absolute++;
                if (prior.TryGetValue(Normalize(t.Title), out MusicTrack? existing))
                {
                    track.HasFile = existing.HasFile;
                    track.FilePath = existing.FilePath;
                    track.Monitored = track.Monitored || existing.Monitored;
                }
                album.Tracks.Add(track);
            }
            album.TotalTrackCount = album.Tracks.Count;

            await db.SaveChangesAsync(ct);
            InvalidateAvailability();
            return await GetAlbumAsync(album.Id, ct);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Adds the whole album list of an artist if missing (matching by title), unmonitored.</summary>
    public async Task AddArtistAlbumsAsync(int artistId, IReadOnlyList<MusicItem> albums, CancellationToken ct = default)
    {
        if (albums.Count == 0)
            return;
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicAlbum> existing = await db.Set<MusicAlbum>().Where(a => a.ArtistId == artistId).ToListAsync(ct);
        foreach (MusicItem album in albums)
        {
            if (string.IsNullOrWhiteSpace(album.Title))
                continue;
            if (existing.Any(a => a.DeezerId == album.DeezerId && album.DeezerId > 0)
                || existing.Any(a => string.Equals(a.Title, album.Title, StringComparison.OrdinalIgnoreCase)))
                continue;
            db.Set<MusicAlbum>().Add(new MusicAlbum
            {
                ArtistId = artistId,
                Title = album.Title,
                AlbumType = album.AlbumType,
                ReleaseDate = album.Year > 0 ? new DateTime(album.Year, 1, 1) : null,
                DeezerId = album.DeezerId,
                Overview = album.Overview,
                PosterUrl = album.ArtUrl.StartsWith("/api/", StringComparison.Ordinal) ? "" : album.ArtUrl,
                TotalTrackCount = album.TrackCount,
                Monitored = false
            });
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Marks the given tracks as present (placed in the library) and drops the cache.</summary>
    public async Task MarkTracksPresentAsync(int albumId, IReadOnlyCollection<int> trackIds, CancellationToken ct = default)
    {
        if (albumId <= 0 || trackIds.Count == 0)
            return;

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicTrack> tracks = await db.Set<MusicTrack>()
            .Where(t => t.AlbumId == albumId && trackIds.Contains(t.Id))
            .ToListAsync(ct);
        if (tracks.Count == 0)
            logger.LogWarning("MarkTracksPresentAsync matched 0 of {Count} track ids for album {AlbumId}", trackIds.Count, albumId);
        foreach (MusicTrack track in tracks)
            track.HasFile = true;
        await db.SaveChangesAsync(ct);
        InvalidateAvailability();
    }

    /// <summary>Marks the given tracks present and records the placed file path for each.</summary>
    public async Task MarkTracksPresentAsync(int albumId, IReadOnlyList<(int TrackId, string FilePath)> placed, CancellationToken ct = default)
    {
        if (albumId <= 0 || placed.Count == 0)
            return;

        Dictionary<int, string> files = placed
            .GroupBy(p => p.TrackId)
            .ToDictionary(g => g.Key, g => g.First().FilePath);

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicTrack> tracks = await db.Set<MusicTrack>()
            .Where(t => t.AlbumId == albumId && files.Keys.Contains(t.Id))
            .ToListAsync(ct);
        foreach (MusicTrack track in tracks)
        {
            track.HasFile = true;
            if (files.TryGetValue(track.Id, out string? path) && !string.IsNullOrEmpty(path))
                track.FilePath = path;
        }
        await db.SaveChangesAsync(ct);
        InvalidateAvailability();
    }

    /// <summary>Records that the rescue attempted to identify this album (so it isn't retried every run).</summary>
    public async Task MarkIdentificationAttemptedAsync(int albumId, CancellationToken ct = default)
    {
        if (albumId <= 0)
            return;
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAlbum? album = await db.Set<MusicAlbum>().FirstOrDefaultAsync(a => a.Id == albumId, ct);
        if (album is null)
            return;
        album.IdentificationAttemptedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Records that the re-tag pass processed this album (so it isn't retried every run).</summary>
    public async Task MarkRetagAttemptedAsync(int albumId, CancellationToken ct = default)
    {
        if (albumId <= 0)
            return;
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAlbum? album = await db.Set<MusicAlbum>().FirstOrDefaultAsync(a => a.Id == albumId, ct);
        if (album is null)
            return;
        album.RetagAttemptedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    // Repair (dedupe / backfill)

    /// <summary>Clears a track's placed file (used when an untrusted file is removed for re-acquisition).</summary>
    public async Task ClearTrackFileAsync(int trackId, CancellationToken ct = default)
    {
        if (trackId <= 0)
            return;
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicTrack? track = await db.Set<MusicTrack>().FirstOrDefaultAsync(t => t.Id == trackId, ct);
        if (track is null)
            return;
        track.HasFile = false;
        track.FilePath = "";
        await db.SaveChangesAsync(ct);
        InvalidateAvailability();
    }

    /// <summary>Records the placed file path for tracks (used when it was resolved later).</summary>
    public async Task SetTrackFilePathsAsync(int albumId, IReadOnlyDictionary<int, string> paths, CancellationToken ct = default)
    {
        if (albumId <= 0 || paths.Count == 0)
            return;
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicTrack> tracks = await db.Set<MusicTrack>()
            .Where(t => t.AlbumId == albumId && paths.Keys.Contains(t.Id))
            .ToListAsync(ct);
        foreach (MusicTrack track in tracks)
        {
            if (paths.TryGetValue(track.Id, out string? path) && !string.IsNullOrEmpty(path))
            {
                track.FilePath = path;
                track.HasFile = true;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Merges albums that are the same release added twice (same Deezer id, or identical artist +
    /// normalised title), e.g. from overlapping import runs. Returns how many duplicates were merged.
    /// Genuine deluxe/compilation repeats are left alone.
    /// </summary>
    public async Task<int> MergeDuplicateAlbumsAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            int merged = 0;

            List<MusicAlbum> albums = await db.Set<MusicAlbum>().Include(a => a.Tracks).ToListAsync(ct);
            foreach (IGrouping<long, MusicAlbum> group in albums.Where(a => a.DeezerId > 0).GroupBy(a => a.DeezerId))
            {
                if (group.Count() > 1)
                    merged += MergeAlbumGroup(db, [.. group]);
            }
            if (merged > 0)
                await db.SaveChangesAsync(ct);

            List<MusicAlbum> remaining = await db.Set<MusicAlbum>().Include(a => a.Tracks).ToListAsync(ct);
            int byTitle = 0;
            foreach (IGrouping<string, MusicAlbum> group in remaining.GroupBy(a => $"{a.ArtistId}|{Normalize(a.Title)}"))
            {
                if (group.Count() < 2)
                    continue;
                // Only merge same-title albums that actually have the same track list, so two different
                // releases that happen to share a name (e.g. two "Greatest Hits") are left alone.
                foreach (IGrouping<string, MusicAlbum> signature in group.GroupBy(TrackSignature))
                {
                    if (signature.Count() > 1)
                        byTitle += MergeAlbumGroup(db, [.. signature]);
                }
            }
            merged += byTitle;

            if (merged > 0)
            {
                await db.SaveChangesAsync(ct);
                InvalidateAvailability();
            }
            return merged;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Normalised, order-independent signature of an album's track titles (for dedupe).</summary>
    static string TrackSignature(MusicAlbum album) =>
        string.Join("|", album.Tracks.Select(t => Normalize(t.Title)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    /// <summary>Keeps the fullest album, moves over the tracks that don't already exist and deletes the rest.</summary>
    static int MergeAlbumGroup(ArrkDbContext db, List<MusicAlbum> group)
    {
        MusicAlbum keep = group.OrderByDescending(a => a.Tracks.Count).ThenBy(a => a.Id).First();
        HashSet<string> keys = keep.Tracks
            .Select(t => TrackKey(t.DiscNumber, t.TrackNumber, t.Title))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int removed = 0;
        foreach (MusicAlbum dup in group.Where(a => a.Id != keep.Id))
        {
            foreach (MusicTrack track in dup.Tracks.ToList())
            {
                string key = TrackKey(track.DiscNumber, track.TrackNumber, track.Title);
                if (keys.Add(key))
                {
                    dup.Tracks.Remove(track);
                    track.AlbumId = keep.Id;
                    keep.Tracks.Add(track);
                }
                else
                {
                    MusicTrack? existing = keep.Tracks.FirstOrDefault(x => TrackKey(x.DiscNumber, x.TrackNumber, x.Title) == key);
                    if (existing is not null && !existing.HasFile && track.HasFile)
                    {
                        existing.HasFile = true;
                        existing.FilePath = track.FilePath;
                    }
                }
            }
            db.Set<MusicAlbum>().Remove(dup);
            removed++;
        }
        return removed;
    }

    /// <summary>Drops exactly-duplicated track rows within an album (same disc + number + title),
    /// keeping the one that has a file. Returns how many rows were removed.</summary>
    public async Task<int> RemoveDuplicateTracksAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
            List<MusicTrack> tracks = await db.Set<MusicTrack>().ToListAsync(ct);
            int removed = 0;
            foreach (IGrouping<string, MusicTrack> group in tracks.GroupBy(t => $"{t.AlbumId}|{TrackKey(t.DiscNumber, t.TrackNumber, t.Title)}"))
            {
                if (group.Count() < 2)
                    continue;
                MusicTrack keep = group.OrderByDescending(t => t.HasFile).ThenBy(t => t.Id).First();
                foreach (MusicTrack dup in group.Where(t => t.Id != keep.Id))
                {
                    db.Set<MusicTrack>().Remove(dup);
                    removed++;
                }
            }
            if (removed > 0)
            {
                await db.SaveChangesAsync(ct);
                InvalidateAvailability();
            }
            return removed;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Gives legacy albums a sensible <see cref="MusicAlbum.Added"/> (migration defaulted to 0001).</summary>
    public async Task<int> BackfillAlbumAddedAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicAlbum> albums = await db.Set<MusicAlbum>()
            .Where(a => a.Added < new DateTime(2000, 1, 1))
            .ToListAsync(ct);
        foreach (MusicAlbum album in albums)
            album.Added = album.ReleaseDate ?? DateTime.UtcNow;
        if (albums.Count > 0)
            await db.SaveChangesAsync(ct);
        return albums.Count;
    }

    /// <summary>Backfills missing per-track Deezer ids (matched from a Deezer album detail).</summary>
    public async Task<int> SetTrackDeezerIdsAsync(int albumId, IReadOnlyDictionary<int, long> deezerByTrackId, CancellationToken ct = default)
    {
        if (albumId <= 0 || deezerByTrackId.Count == 0)
            return 0;

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicTrack> tracks = await db.Set<MusicTrack>()
            .Where(t => t.AlbumId == albumId && deezerByTrackId.Keys.Contains(t.Id))
            .ToListAsync(ct);
        int updated = 0;
        foreach (MusicTrack track in tracks)
        {
            if (track.DeezerId <= 0 && deezerByTrackId.TryGetValue(track.Id, out long id) && id > 0)
            {
                track.DeezerId = id;
                updated++;
            }
        }
        if (updated > 0)
            await db.SaveChangesAsync(ct);
        return updated;
    }

    /// <summary>
    /// Reconciles albums that entered the library only because one of their songs was grabbed, by a
    /// playlist auto-download or a single-song grab. An album is explicit when a
    /// <see cref="LibraryIntent"/> names it or its artist. Anything else with a recorded song grab is
    /// implicit, so its tracks are unmonitored and the album reads as Partial rather than Missing.
    /// Idempotent; returns how many albums were reconciled.
    /// </summary>
    public async Task<int> ReconcileImplicitAlbumMonitoringAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);

            HashSet<int> explicitAlbums = (await db.Set<LibraryIntent>().AsNoTracking()
                .Where(i => i.Kind == LibraryIntentKind.Album && i.State != LibraryIntentState.Ignored)
                .Select(i => i.TargetId)
                .ToListAsync(ct)).ToHashSet();
            HashSet<int> explicitArtists = (await db.Set<LibraryIntent>().AsNoTracking()
                .Where(i => i.Kind == LibraryIntentKind.Artist && i.State != LibraryIntentState.Ignored)
                .Select(i => i.TargetId)
                .ToListAsync(ct)).ToHashSet();
            HashSet<long> grabbedTracks = (await db.Set<GrabRecord>().AsNoTracking()
                .Where(g => g.DeezerTrackId > 0)
                .Select(g => g.DeezerTrackId)
                .ToListAsync(ct)).ToHashSet();
            if (grabbedTracks.Count == 0)
                return 0;

            List<MusicAlbum> albums = await db.Set<MusicAlbum>().Include(a => a.Tracks).ToListAsync(ct);
            int reconciled = 0;
            foreach (MusicAlbum album in albums)
            {
                if (!album.Monitored || album.Tracks.Count == 0)
                    continue;
                int placed = album.Tracks.Count(t => t.HasFile);
                int total = Math.Max(album.TotalTrackCount, album.Tracks.Count);
                if (placed == 0 || placed >= total)
                    continue;
                if (explicitAlbums.Contains(album.Id) || explicitArtists.Contains(album.ArtistId))
                    continue;
                if (!album.Tracks.Any(t => t.DeezerId > 0 && grabbedTracks.Contains(t.DeezerId)))
                    continue;

                album.Monitored = false;
                foreach (MusicTrack track in album.Tracks)
                    track.Monitored = false;
                reconciled++;
            }
            if (reconciled > 0)
                await db.SaveChangesAsync(ct);
            return reconciled;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SetAlbumMonitoredAsync(int albumId, bool monitored, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAlbum? album = await db.Set<MusicAlbum>().Include(a => a.Tracks).Include(a => a.Artist)
            .FirstOrDefaultAsync(a => a.Id == albumId, ct);
        if (album is null)
            return;
        album.Monitored = monitored;
        // Monitoring an album means we want all of it; unmonitoring relaxes every track.
        foreach (MusicTrack track in album.Tracks)
            track.Monitored = monitored;

        // Keep the intent in step so the implicit-album reconcile can tell a deliberate "monitor" from
        // an album that only exists because one of its songs was grabbed.
        LibraryIntent? intent = await db.Set<LibraryIntent>()
            .FirstOrDefaultAsync(i => i.Kind == LibraryIntentKind.Album && i.TargetId == albumId, ct);
        if (monitored)
        {
            if (intent is null)
                db.Set<LibraryIntent>().Add(new LibraryIntent
                {
                    Kind = LibraryIntentKind.Album,
                    TargetId = albumId,
                    State = LibraryIntentState.Wanted,
                    Source = "user",
                    Title = album.Title,
                    Artist = album.Artist?.Name ?? "",
                    RequestedAt = DateTime.UtcNow
                });
            else if (intent.State == LibraryIntentState.Ignored)
                intent.State = LibraryIntentState.Wanted;
        }
        else if (intent is not null)
        {
            intent.State = LibraryIntentState.Ignored;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SetArtistMonitoredAsync(int artistId, bool monitored, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicArtist? artist = await db.Set<MusicArtist>().FirstOrDefaultAsync(a => a.Id == artistId, ct);
        if (artist is null)
            return;
        artist.Monitored = monitored;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAlbumAsync(int albumId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAlbum? album = await db.Set<MusicAlbum>().FirstOrDefaultAsync(a => a.Id == albumId, ct);
        if (album is null)
            return;
        // Drop the grabs that pointed at this album so a later re-add doesn't inherit a stale mapping.
        foreach (GrabRecord grab in await db.Set<GrabRecord>().Where(g => g.AlbumId == albumId).ToListAsync(ct))
            db.Set<GrabRecord>().Remove(grab);
        db.Set<MusicAlbum>().Remove(album);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Resolves the album a torrent belongs to from its name (used when no grab record maps it, e.g.
    /// a torrent we didn't grab ourselves, or one whose album was deleted and re-added).
    /// </summary>
    public async Task<Album?> FindAlbumByTorrentNameAsync(string torrentName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(torrentName))
            return null;

        string norm = Normalize(torrentName);
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicAlbum> albums = await db.Set<MusicAlbum>()
            .Include(a => a.Artist)
            .Include(a => a.Tracks)
            .AsNoTracking()
            .ToListAsync(ct);

        MusicAlbum? match = albums
            .Where(a => Normalize(a.Title).Length >= 3
                && (norm.Contains(Normalize(a.Title), StringComparison.Ordinal)
                    || Normalize(a.Title).Contains(norm, StringComparison.Ordinal)))
            .OrderByDescending(a => Normalize(a.Title).Length)
            .FirstOrDefault();
        if (match is null)
            return null;

        (Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum) = await GetAvailabilityAsync(ct);
        return ToAlbum(match, ResolveFileCount(match, byAlbumArtist, byAlbum));
    }

    public async Task DeleteArtistAsync(int artistId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicArtist? artist = await db.Set<MusicArtist>().FirstOrDefaultAsync(a => a.Id == artistId, ct);
        if (artist is null)
            return;
        db.Set<MusicArtist>().Remove(artist);
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordIntentAsync(
        LibraryIntentKind kind, int targetId, string title, string artist, int qualityProfileId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        bool exists = await db.Set<LibraryIntent>().AnyAsync(
            i => i.Kind == kind && i.TargetId == targetId && i.State != LibraryIntentState.Ignored, ct);
        if (exists)
            return;
        db.Set<LibraryIntent>().Add(new LibraryIntent
        {
            Kind = kind,
            TargetId = targetId,
            State = LibraryIntentState.Wanted,
            QualityProfileId = qualityProfileId,
            Source = "deezer",
            Title = title,
            Artist = artist,
            RequestedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    // Acquisition queue

    /// <summary>
    /// Adds an acquisition request (a song or album we want). Idempotent: an existing row keeps its
    /// attempt count, so re-requesting never resets how far it has fallen behind.
    /// </summary>
    public async Task EnqueueAcquireAsync(
        string artist, string title, int albumId, long deezerTrackId, bool requireArtist, CancellationToken ct = default)
    {
        string key = AcquireKey(albumId, deezerTrackId, artist, title);
        if (key.Length == 0)
            return;

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Set<MusicAcquireQueueItem>().AnyAsync(q => q.Key == key, ct))
            return;

        db.Set<MusicAcquireQueueItem>().Add(new MusicAcquireQueueItem
        {
            Key = key,
            Artist = artist ?? "",
            Title = title ?? "",
            AlbumId = albumId > 0 ? albumId : 0,
            DeezerTrackId = deezerTrackId,
            RequireArtist = requireArtist,
            RequestedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Marks a request failed: bumps its attempt count so it drops behind healthier requests.</summary>
    public async Task RequeueAcquireAsync(
        string artist, string title, int albumId, long deezerTrackId, bool requireArtist, string error, CancellationToken ct = default)
    {
        string key = AcquireKey(albumId, deezerTrackId, artist, title);
        if (key.Length == 0)
            return;

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAcquireQueueItem? item = await db.Set<MusicAcquireQueueItem>().FirstOrDefaultAsync(q => q.Key == key, ct);
        DateTime now = DateTime.UtcNow;
        if (item is null)
        {
            db.Set<MusicAcquireQueueItem>().Add(new MusicAcquireQueueItem
            {
                Key = key,
                Artist = artist ?? "",
                Title = title ?? "",
                AlbumId = albumId > 0 ? albumId : 0,
                DeezerTrackId = deezerTrackId,
                RequireArtist = requireArtist,
                Attempts = 1,
                LastError = error,
                RequestedAt = now,
                LastAttemptAt = now
            });
        }
        else
        {
            item.Attempts++;
            item.InFlight = false;
            item.LastError = error;
            item.LastAttemptAt = now;
            LogExhausted(item);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Re-arms a request after an explicit search, so a parked (attempt-capped) song/album gets a
    /// fresh set of automatic attempts instead of staying stuck forever.
    /// </summary>
    public async Task RearmAcquireAsync(
        string artist, string title, int albumId, long deezerTrackId, CancellationToken ct = default)
    {
        string key = AcquireKey(albumId, deezerTrackId, artist, title);
        if (key.Length == 0)
            return;

        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAcquireQueueItem? item = await db.Set<MusicAcquireQueueItem>().FirstOrDefaultAsync(q => q.Key == key, ct);
        if (item is null || item.Attempts == 0)
            return;

        logger.LogInformation(
            "Explicit search re-arms music request \"{Title}\" by {Artist} after {Attempts} attempt(s)",
            item.Title, item.Artist, item.Attempts);
        item.Attempts = 0;
        item.LastError = "";
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The next batch to try: least-failed first, then oldest. Parked requests (already at
    /// <see cref="MusicAcquireQueueItem.MaxAttempts"/>) are skipped, and an in-flight row older than
    /// <see cref="MusicAcquireQueueItem.StaleInFlightHours"/> is treated as abandoned so a lost grab or
    /// import cannot stall a request forever.</summary>
    public async Task<List<MusicAcquireQueueItem>> TakeAcquireBatchAsync(int count, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        DateTime staleBefore = DateTime.UtcNow.AddHours(-MusicAcquireQueueItem.StaleInFlightHours);
        return await db.Set<MusicAcquireQueueItem>().AsNoTracking()
            .Where(q => (!q.InFlight || (q.LastAttemptAt ?? q.RequestedAt) < staleBefore)
                && q.Attempts < MusicAcquireQueueItem.MaxAttempts)
            .OrderBy(q => q.Attempts)
            .ThenBy(q => q.LastAttemptAt ?? q.RequestedAt)
            .ThenBy(q => q.Id)
            .Take(Math.Max(1, count))
            .ToListAsync(ct);
    }

    /// <summary>Records that a grab is in flight for a request (so the drain job leaves it alone).</summary>
    public async Task MarkAcquireInFlightAsync(int id, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAcquireQueueItem? item = await db.Set<MusicAcquireQueueItem>().FirstOrDefaultAsync(q => q.Id == id, ct);
        if (item is null)
            return;
        item.InFlight = true;
        item.LastAttemptAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Records a failed attempt: bumps the count so the request drops down the queue.</summary>
    public async Task RecordAcquireFailureAsync(int id, string error, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MusicAcquireQueueItem? item = await db.Set<MusicAcquireQueueItem>().FirstOrDefaultAsync(q => q.Id == id, ct);
        if (item is null)
            return;
        item.Attempts++;
        item.InFlight = false;
        item.LastError = error;
        item.LastAttemptAt = DateTime.UtcNow;
        LogExhausted(item);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Logs once when a request reaches the attempt cap and is parked.</summary>
    void LogExhausted(MusicAcquireQueueItem item)
    {
        if (item.Attempts != MusicAcquireQueueItem.MaxAttempts)
            return;
        logger.LogWarning(
            "Parking music request \"{Title}\" by {Artist} after {Attempts} attempts (last error: {Error}); search it explicitly to retry",
            item.Title, item.Artist, item.Attempts, item.LastError);
    }

    /// <summary>
    /// True when the library already holds what a queue row asks for, so the drain job can drop the
    /// row instead of downloading it again. Needed because a playlist re-sync can re-queue a song
    /// between the import and Navidrome's next scan: the row then kept grabbing another copy of a song
    /// we already held (the live "infinite download" loop). Album rows require every track to have a
    /// file; song rows match the exact Deezer id first, then the cleaned title + artist (the requested
    /// edition and the held edition often carry different ids and edition tails).
    /// </summary>
    public async Task<bool> IsAcquireSatisfiedAsync(MusicAcquireQueueItem item, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);

        if (item.DeezerTrackId > 0
            && await db.Set<MusicTrack>().AnyAsync(t => t.DeezerId == item.DeezerTrackId && t.HasFile, ct))
        {
            return true;
        }

        if (item.AlbumId > 0 && item.DeezerTrackId <= 0)
        {
            MusicAlbum? album = await db.Set<MusicAlbum>().AsNoTracking()
                .Include(a => a.Tracks)
                .FirstOrDefaultAsync(a => a.Id == item.AlbumId, ct);
            return album is { Tracks.Count: > 0 } && album.Tracks.All(t => t.HasFile);
        }

        if (string.IsNullOrWhiteSpace(item.Title))
            return false;

        string wantedTitle = Normalize(MusicText.CleanTrackTitle(item.Title));
        if (wantedTitle.Length == 0)
            return false;
        string wantedArtist = Normalize(item.Artist);

        // Only this artist's held tracks can satisfy the request; compare the cleaned titles in memory
        // (the editions spell the same tail differently: "(Remastered 2007)" vs "(2007 Remaster)").
        List<string> heldTitles = await (
            from track in db.Set<MusicTrack>()
            join album in db.Set<MusicAlbum>() on track.AlbumId equals album.Id
            join artist in db.Set<MusicArtist>() on album.ArtistId equals artist.Id
            where track.HasFile
                && (wantedArtist.Length == 0 || artist.Name.ToLower() == item.Artist.ToLower())
            select track.Title).AsNoTracking().ToListAsync(ct);

        return heldTitles.Any(title => Normalize(MusicText.CleanTrackTitle(title)) == wantedTitle);
    }

    /// <summary>Removes queue rows once we actually hold the request (after import).</summary>
    public async Task CompleteAcquireAsync(int albumId, long deezerTrackId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        List<MusicAcquireQueueItem> items = await db.Set<MusicAcquireQueueItem>()
            .Where(q => (deezerTrackId > 0 && q.DeezerTrackId == deezerTrackId) || (albumId > 0 && q.AlbumId == albumId))
            .ToListAsync(ct);
        if (items.Count == 0)
            return;
        db.Set<MusicAcquireQueueItem>().RemoveRange(items);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> CountAcquireQueueAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<MusicAcquireQueueItem>().CountAsync(ct);
    }

    /// <summary>Queue counts for the live activity view: total pending, currently searching, retrying.</summary>
    public async Task<(int Total, int InFlight, int Retrying)> GetAcquireQueueSummaryAsync(CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbFactory.CreateDbContextAsync(ct);
        int total = await db.Set<MusicAcquireQueueItem>().CountAsync(ct);
        int inFlight = await db.Set<MusicAcquireQueueItem>().CountAsync(q => q.InFlight, ct);
        int retrying = await db.Set<MusicAcquireQueueItem>().CountAsync(
            q => q.Attempts > 0 && q.Attempts < MusicAcquireQueueItem.MaxAttempts, ct);
        return (total, inFlight, retrying);
    }

    /// <summary>Stable identity of an acquisition request (a song by its Deezer track id, an album by
    /// its library id, else by artist+title).</summary>
    static string AcquireKey(int albumId, long deezerTrackId, string artist, string title)
    {
        if (deezerTrackId > 0)
            return $"track:{deezerTrackId}";
        if (albumId > 0)
            return $"album:{albumId}";
        string a = Normalize(artist ?? "");
        string t = Normalize(title ?? "");
        return t.Length == 0 && a.Length == 0 ? "" : $"name:{a}|{t}";
    }

    // Lookups

    static async Task<MusicArtist?> FindArtistEntityAsync(ArrkDbContext db, MusicItem artist, CancellationToken ct)
    {
        string name = !string.IsNullOrWhiteSpace(artist.Title) ? artist.Title : artist.ArtistName;
        if (artist.DeezerId > 0)
        {
            MusicArtist? byDeezer = await db.Set<MusicArtist>().FirstOrDefaultAsync(a => a.DeezerId == artist.DeezerId, ct);
            if (byDeezer is not null)
                return byDeezer;
        }

        string key = Normalize(name);
        List<MusicArtist> all = await db.Set<MusicArtist>().ToListAsync(ct);
        return all.FirstOrDefault(a => Normalize(a.Name) == key);
    }

    static async Task<MusicAlbum?> FindAlbumEntityAsync(ArrkDbContext db, int artistId, MusicItem album, CancellationToken ct)
    {
        if (album.DeezerId > 0)
        {
            MusicAlbum? byDeezer = await db.Set<MusicAlbum>().Include(a => a.Tracks)
                .FirstOrDefaultAsync(a => a.DeezerId == album.DeezerId, ct);
            if (byDeezer is not null)
                return byDeezer;
        }

        string titleKey = Normalize(album.Title);
        List<MusicAlbum> albums = await db.Set<MusicAlbum>().Include(a => a.Tracks)
            .Where(a => a.ArtistId == artistId).ToListAsync(ct);
        return albums.FirstOrDefault(a => Normalize(a.Title) == titleKey);
    }

    // Availability (Navidrome)

    async Task<(Dictionary<string, int> ByAlbumArtist, Dictionary<string, int> ByAlbum)> GetAvailabilityAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _availabilityAt < AvailabilityTtl)
                return (_availabilityByAlbumArtist, _availabilityByAlbum);
        }

        Dictionary<string, int> byAlbumArtist = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> byAlbum = new(StringComparer.OrdinalIgnoreCase);
        if (navidromeConfig.CurrentValue.Enabled)
        {
            try
            {
                const int pageSize = 500;
                for (int offset = 0; offset < 10000; offset += pageSize)
                {
                    List<SubsonicAlbum> page = await navidromeClient.GetAlbumListAsync("alphabeticalByName", pageSize, offset, ct);
                    foreach (SubsonicAlbum album in page)
                    {
                        if (string.IsNullOrWhiteSpace(album.Name))
                            continue;
                        byAlbumArtist[$"{Normalize(album.Artist)}|{Normalize(album.Name)}"] = album.SongCount;
                        string albumKey = Normalize(album.Name);
                        byAlbum[albumKey] = Math.Max(byAlbum.GetValueOrDefault(albumKey), album.SongCount);
                    }
                    if (page.Count < pageSize)
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read the Navidrome album list for availability");
            }
        }

        lock (_gate)
        {
            _availabilityByAlbumArtist = byAlbumArtist;
            _availabilityByAlbum = byAlbum;
            _availabilityAt = DateTime.UtcNow;
            return (byAlbumArtist, byAlbum);
        }
    }

    static int ResolveFileCount(MusicAlbum album, Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum)
    {
        string artist = Normalize(album.Artist?.Name ?? "");
        string title = Normalize(album.Title);
        if (byAlbumArtist.TryGetValue($"{artist}|{title}", out int exact))
            return exact;
        // Fall back to a title-only match (Navidrome's artist credit may differ from Deezer's).
        return byAlbum.TryGetValue(title, out int byTitle) ? byTitle : 0;
    }

    // Mapping

    Artist ToArtist(MusicArtist artist, Dictionary<string, int> byAlbumArtist, Dictionary<string, int> byAlbum)
    {
        List<Album> albums = artist.Albums
            .OrderBy(a => a.ReleaseDate ?? DateTime.MaxValue)
            .Select(a => ToAlbum(a, ResolveFileCount(a, byAlbumArtist, byAlbum)))
            .ToList();

        int totalTracks = albums.Sum(a => a.TotalTrackCount);
        int fileTracks = albums.Sum(a => a.TrackFileCount);

        return new Artist
        {
            Id = artist.Id,
            Name = artist.Name,
            SortName = artist.SortName,
            Overview = artist.Overview,
            Genres = ParseGenres(artist.GenresJson),
            Rating = artist.Rating,
            Status = ArtistStatus.Continuing,
            ArtistType = "Artist",
            Monitored = artist.Monitored,
            Added = artist.Added,
            DeezerId = artist.DeezerId,
            Path = "",
            PosterUrl = artist.PosterUrl,
            FanartUrl = artist.FanartUrl,
            AlbumCount = albums.Count,
            TrackFileCount = fileTracks,
            TrackCount = totalTracks,
            TotalTrackCount = totalTracks,
            SizeOnDisk = fileTracks,
            Albums = albums
        };
    }

    static Album ToAlbum(MusicAlbum album, int fileCount)
    {
        int trackCount = album.Tracks.Count;
        int placed = album.Tracks.Count(t => t.HasFile);
        // Prefer recorded placement over the Navidrome count. A loose scan count can exceed what was
        // actually placed, which makes the album count disagree with the Songs view.
        int effectiveFiles = placed > 0 ? placed : fileCount;
        int total = Math.Max(album.TotalTrackCount, trackCount);
        return new Album
        {
            Id = album.Id,
            ArtistId = album.ArtistId,
            ArtistName = album.Artist?.Name ?? "",
            Title = album.Title,
            Overview = album.Overview,
            AlbumType = album.AlbumType,
            ReleaseDate = album.ReleaseDate,
            Monitored = album.Monitored,
            DeezerId = album.DeezerId,
            ArtistDeezerId = album.Artist?.DeezerId ?? 0,
            DurationSeconds = album.DurationSeconds,
            Added = album.Added,
            IdentificationAttemptedAt = album.IdentificationAttemptedAt,
            RetagAttemptedAt = album.RetagAttemptedAt,
            TrackCount = trackCount,
            TrackFileCount = effectiveFiles,
            TotalTrackCount = total,
            SizeOnDisk = effectiveFiles,
            PosterUrl = album.PosterUrl,
            Tracks = album.Tracks
                .OrderBy(t => t.DiscNumber).ThenBy(t => t.AbsoluteTrackNumber).ThenBy(t => t.Id)
                .Select(t => ToTrack(t, t.HasFile || (effectiveFiles > 0 && effectiveFiles >= total)))
                .ToList()
        };
    }

    static Track ToTrack(MusicTrack track, bool hasFile) => new()
    {
        Id = track.Id,
        AlbumId = track.AlbumId,
        ArtistId = track.ArtistId,
        Title = track.Title,
        TrackNumber = track.TrackNumber,
        AbsoluteTrackNumber = track.AbsoluteTrackNumber,
        MediumNumber = track.DiscNumber,
        DurationSeconds = track.DurationSeconds,
        Monitored = track.Monitored,
        HasFile = hasFile,
        DeezerId = track.DeezerId,
        FilePath = track.FilePath,
        LyricsState = track.LyricsState,
        LyricsTier = track.LyricsTier
    };

    static List<string> ParseGenres(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    static string StripArticle(string name)
    {
        foreach (string article in (string[])["The ", "A ", "An "])
        {
            if (name.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                return name[article.Length..] + ", " + article.Trim();
        }
        return name;
    }

    static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Identity of a track within an album (disc + track number + normalised title).</summary>
    static string TrackKey(int disc, int number, string title) =>
        $"{disc}|{number}|{Normalize(title)}";
}
