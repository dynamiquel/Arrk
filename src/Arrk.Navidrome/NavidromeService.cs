using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Navidrome;

/// <summary>
/// App-facing wrapper over the Navidrome Subsonic client: library shelves, playlists and playback.
/// Keeps the enable/credential checks in one place so the UI degrades gracefully when Navidrome is
/// off or not yet configured.
/// </summary>
public class NavidromeService(
    NavidromeClient client,
    NavidromeApiSecret secret,
    IOptionsMonitor<NavidromeConfig> config,
    ILogger<NavidromeService> logger)
{
    /// <summary>Lyrics only change when the library is rescanned, so a found record is cached; a
    /// miss is not, so newly written sidecars appear as soon as Navidrome has indexed them.</summary>
    static readonly TimeSpan LyricsCacheTtl = TimeSpan.FromMinutes(30);
    const int MaxLyricsCacheEntries = 500;

    readonly ConcurrentDictionary<string, (DateTime At, List<SubsonicStructuredLyrics> Tracks)> _lyricsCache = new();

    NavidromeConfig Config => config.CurrentValue;

    public bool IsEnabled => Config.Enabled;

    public bool IsPlayerEnabled => Config.Enabled && Config.WebPlayer;

    public bool IsConfigured => Config.Enabled && !string.IsNullOrWhiteSpace(secret.Password);

    public async Task<List<SubsonicAlbum>> GetRecentlyAddedAsync(int size = 30, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        try { return await client.GetAlbumListAsync("newest", size, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Navidrome getAlbumList2(newest) failed"); return []; }
    }

    /// <summary>Asks Navidrome to re-scan the music folder (after Arrk re-tags files).</summary>
    public async Task StartScanAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        try { await client.StartScanAsync(ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Navidrome startScan failed"); }
    }

    public async Task<List<SubsonicAlbum>> GetRecentlyPlayedAsync(int size = 30, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        try { return await client.GetAlbumListAsync("recent", size, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Navidrome getAlbumList2(recent) failed"); return []; }
    }

    public async Task<SubsonicAlbumWithSongs?> GetAlbumAsync(string id, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try { return await client.GetAlbumAsync(id, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Navidrome getAlbum failed"); return null; }
    }

    public async Task<List<SubsonicPlaylist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return [];
        try { return await client.GetPlaylistsAsync(ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Navidrome getPlaylists failed"); return []; }
    }

    public async Task<SubsonicPlaylist?> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try { return await client.GetPlaylistAsync(id, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "Navidrome getPlaylist failed"); return null; }
    }

    public async Task<string?> CreatePlaylistAsync(string name, IEnumerable<string> songIds, CancellationToken ct = default) =>
        await client.CreatePlaylistAsync(name, songIds, ct);

    /// <summary>Creates or replaces a playlist's tracklist (used when syncing a subscription).</summary>
    public async Task<string?> CreateOrReplacePlaylistAsync(string? playlistId, string name, IEnumerable<string> songIds, CancellationToken ct = default) =>
        await client.CreatePlaylistAsync(playlistId, name, songIds, ct);

    public async Task RenamePlaylistAsync(string id, string name, CancellationToken ct = default) =>
        await client.RenamePlaylistAsync(id, name, ct);

    public async Task AddToPlaylistAsync(string id, IEnumerable<string> songIds, CancellationToken ct = default) =>
        await client.AddToPlaylistAsync(id, songIds, ct);

    public async Task RemoveFromPlaylistAsync(string id, int index, CancellationToken ct = default) =>
        await client.RemoveFromPlaylistAsync(id, index, ct);

    public async Task DeletePlaylistAsync(string id, CancellationToken ct = default) =>
        await client.DeletePlaylistAsync(id, ct);

    public Task ScrobbleAsync(string songId, bool submission = true, CancellationToken ct = default) =>
        client.ScrobbleAsync(songId, submission, ct);

    public Task SavePlayQueueAsync(IEnumerable<string> songIds, string? current, long positionMs, CancellationToken ct = default) =>
        client.SavePlayQueueAsync(songIds, current, positionMs, ct);

    public async Task<SubsonicPlayQueue?> GetPlayQueueAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try { return await client.GetPlayQueueAsync(ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Navidrome getPlayQueue failed"); return null; }
    }

    /// <summary>
    /// A song's structured lyrics, cached once found. Returns an empty list when the song has none
    /// (or Navidrome is off), so the player can show a friendly empty state.
    /// </summary>
    public async Task<List<SubsonicStructuredLyrics>> GetLyricsAsync(string songId, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(songId))
            return [];

        if (_lyricsCache.TryGetValue(songId, out (DateTime At, List<SubsonicStructuredLyrics> Tracks) cached)
            && DateTime.UtcNow - cached.At < LyricsCacheTtl)
            return cached.Tracks;

        try
        {
            List<SubsonicStructuredLyrics> tracks = await client.GetLyricsBySongIdAsync(songId, enhanced: true, ct);
            // Cache only hits: a miss must be re-checked so a sidecar written by the lyrics job shows
            // up as soon as Navidrome has rescanned, instead of being pinned empty for half an hour.
            if (tracks.Count > 0)
            {
                if (_lyricsCache.Count >= MaxLyricsCacheEntries)
                    _lyricsCache.Clear();
                _lyricsCache[songId] = (DateTime.UtcNow, tracks);
            }
            return tracks;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Navidrome lyrics lookup failed for {SongId}", songId);
            return [];
        }
    }

    /// <summary>Searches Navidrome (Subsonic search3) for songs, used when adding to playlists.</summary>
    public async Task<List<SubsonicSong>> SearchSongsAsync(string query, int count = 20, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(query)) return [];
        try { return await client.SearchSongsAsync(query, count, ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Navidrome song search failed"); return []; }
    }

    /// <summary>
    /// Finds the Navidrome song id for a title/artist so a library track can be played here. The
    /// ARTIST must match whenever we have one (otherwise a loose title search returns the wrong song;
    /// "Easy" matching "So Easy"); an exact title is accepted without an artist match only when it is
    /// unique. Never falls back to an arbitrary first result.
    /// </summary>
    public async Task<string?> FindSongIdAsync(string title, string artist, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            List<SubsonicSong> songs = await client.SearchSongsAsync(title, 40, ct);
            string wantedTitle = Normalize(title);
            string wantedArtist = Normalize(artist ?? "");
            if (wantedTitle.Length == 0) return null;

            bool ArtistMatches(SubsonicSong s)
            {
                if (wantedArtist.Length == 0)
                    return true;
                string candidate = Normalize(s.Artist);
                return candidate.Length > 0
                    && (candidate.Contains(wantedArtist, StringComparison.Ordinal) || wantedArtist.Contains(candidate, StringComparison.Ordinal));
            }
            static bool TitleExact(SubsonicSong s, string wanted) => Normalize(s.Title) == wanted;
            static bool TitleClose(SubsonicSong s, string wanted) =>
                Normalize(s.Title).EndsWith(wanted, StringComparison.Ordinal) || Normalize(s.Title).Contains(wanted, StringComparison.Ordinal);

            List<SubsonicSong> exact = [.. songs.Where(s => TitleExact(s, wantedTitle))];
            SubsonicSong? best =
                exact.FirstOrDefault(ArtistMatches)
                ?? exact.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Artist))  // unknown artist tag only
                ?? songs.FirstOrDefault(s => TitleClose(s, wantedTitle) && ArtistMatches(s));
            return best?.Id;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Navidrome song lookup failed for {Title}", title);
            return null;
        }
    }

    static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Finds the Navidrome album id for an album/artist so a whole library album can be played.
    /// Prefers an artist match; an exact album name is accepted without one only when it is unique.</summary>
    public async Task<string?> FindAlbumIdAsync(string album, string artist, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(album)) return null;
        try
        {
            List<SubsonicAlbum> albums = await client.SearchAlbumsAsync(album, 15, ct);
            string wanted = Normalize(album);
            string wantedArtist = Normalize(artist ?? "");
            if (wanted.Length == 0) return null;

            bool ArtistMatches(SubsonicAlbum a)
            {
                if (wantedArtist.Length == 0)
                    return true;
                string candidate = Normalize(a.Artist);
                return candidate.Length > 0
                    && (candidate.Contains(wantedArtist, StringComparison.Ordinal) || wantedArtist.Contains(candidate, StringComparison.Ordinal));
            }

            List<SubsonicAlbum> exact = [.. albums.Where(a => Normalize(a.Name) == wanted)];
            SubsonicAlbum? best =
                exact.FirstOrDefault(ArtistMatches)
                ?? exact.FirstOrDefault(a => string.IsNullOrWhiteSpace(a.Artist))
                ?? albums.FirstOrDefault(a => Normalize(a.Name).StartsWith(wanted, StringComparison.Ordinal) && ArtistMatches(a))
                ?? albums.FirstOrDefault(a => Normalize(a.Name).Contains(wanted, StringComparison.Ordinal) && ArtistMatches(a));
            return best?.Id;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Navidrome album lookup failed for {Album}", album);
            return null;
        }
    }

    /// <summary>Browser-playable URL through Arrk's stream proxy for a Navidrome song id.</summary>
    public static string PlayerStreamUrl(string songId) =>
        $"/api/music/player/stream/{Uri.EscapeDataString(songId)}";

    /// <summary>Cover-art URL through Arrk's image proxy (avoids exposing Navidrome creds).</summary>
    public static string CoverArtUrl(string coverArtId) =>
        string.IsNullOrEmpty(coverArtId) ? "" : $"/api/music/player/cover/{Uri.EscapeDataString(coverArtId)}";
}
