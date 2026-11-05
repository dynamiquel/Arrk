using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stackarr.Navidrome;

/// <summary>
/// Thin client for the Navidrome Subsonic API (<c>/rest/…</c>). Used for library browsing,
/// playlists and playback. Authenticates with the Subsonic token scheme (salt + MD5 of
/// password+salt).
/// </summary>
public class NavidromeClient
{
    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    readonly HttpClient _http;
    readonly Func<string> _baseUrl;
    readonly string _username;
    readonly string _password;

    public NavidromeClient(string url, string username, string password)
        : this(() => url, username, password, null)
    {
    }

    public NavidromeClient(string url, string username, string password, HttpMessageHandler? handler)
        : this(() => url, username, password, handler)
    {
    }

    /// <summary>
    /// Base-URL-resolving constructor: the URL (including the <c>/navidrome</c> sub-path, which the
    /// container's <c>ND_BASEURL</c> follows) is resolved from a delegate so toggling
    /// <c>services.navidrome.proxy</c> applies without a restart.
    /// </summary>
    public NavidromeClient(Func<string> baseUrl, string username, string password, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl;
        _username = username;
        _password = password;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>The current Subsonic base URL (always with a trailing slash).</summary>
    public string BaseUrl
    {
        get
        {
            string url = _baseUrl();
            return url.EndsWith('/') ? url : url + "/";
        }
    }

    string AuthQuery()
    {
        string salt = Guid.NewGuid().ToString("N")[..12];
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(_password + salt));
        string token = Convert.ToHexStringLower(hash);
        return $"u={Uri.EscapeDataString(_username)}&t={token}&s={salt}&v=1.16.1&c=Stackarr&f=json";
    }

    /// <summary>Absolute URL for a Subsonic method, with auth (used by the server-side stream proxy).</summary>
    public string BuildUrl(string method, string? extra = null)
    {
        string query = AuthQuery();
        if (!string.IsNullOrEmpty(extra))
            query += "&" + extra;
        return $"{BaseUrl}rest/{method}?{query}";
    }

    async Task<SubsonicResponse?> CallAsync(string method, string? extra, CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.GetAsync(BuildUrl(method, extra), ct);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<SubsonicResponse>(json, JsonOpts);
    }

    /// <summary>True when the configured credentials authenticate against Navidrome (Subsonic ping).</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            SubsonicResponse? r = await CallAsync("ping", null, ct);
            return string.Equals(r?.Response?.Status, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // Browsing

    public Task<List<SubsonicAlbum>> GetAlbumListAsync(string type, int size = 50, CancellationToken ct = default)
        => GetAlbumListAsync(type, size, 0, ct);

    /// <summary>Asks Navidrome to re-scan the music folder (used after Stackarr re-tags files).</summary>
    public async Task StartScanAsync(CancellationToken ct = default) =>
        await CallAsync("startScan", null, ct);

    public async Task<List<SubsonicAlbum>> GetAlbumListAsync(string type, int size, int offset, CancellationToken ct = default)
    {
        string extra = $"type={type}&size={size}";
        if (offset > 0)
            extra += $"&offset={offset}";
        SubsonicResponse? r = await CallAsync("getAlbumList2", extra, ct);
        return r?.Response?.AlbumList2?.Album ?? [];
    }

    public async Task<List<SubsonicArtist>> GetArtistsAsync(CancellationToken ct = default)
    {
        SubsonicResponse? r = await CallAsync("getArtists", null, ct);
        return r?.Response?.Artists?.Index?.SelectMany(i => i.Artist ?? []).ToList() ?? [];
    }

    public async Task<SubsonicAlbumWithSongs?> GetAlbumAsync(string id, CancellationToken ct = default)
    {
        SubsonicResponse? r = await CallAsync("getAlbum", $"id={Uri.EscapeDataString(id)}", ct);
        return r?.Response?.Album;
    }

    public async Task<List<SubsonicSong>> SearchSongsAsync(string query, int count = 20, CancellationToken ct = default)
    {
        SubsonicResponse? r = await CallAsync("search3", $"query={Uri.EscapeDataString(query)}&songCount={count}&albumCount=0&artistCount=0", ct);
        return r?.Response?.SearchResult3?.Song ?? [];
    }

    public async Task<List<SubsonicAlbum>> SearchAlbumsAsync(string query, int count = 10, CancellationToken ct = default)
    {
        SubsonicResponse? r = await CallAsync("search3", $"query={Uri.EscapeDataString(query)}&songCount=0&albumCount={count}&artistCount=0", ct);
        return r?.Response?.SearchResult3?.Album ?? [];
    }

    // Playlists

    public async Task<List<SubsonicPlaylist>> GetPlaylistsAsync(CancellationToken ct = default)
    {
        SubsonicResponse? r = await CallAsync("getPlaylists", null, ct);
        return r?.Response?.Playlists?.Playlist ?? [];
    }

    public async Task<SubsonicPlaylist?> GetPlaylistAsync(string id, CancellationToken ct = default)
    {
        SubsonicResponse? r = await CallAsync("getPlaylist", $"id={Uri.EscapeDataString(id)}", ct);
        return r?.Response?.Playlist;
    }

    /// <summary>Creates a playlist and returns its Navidrome id (used to record per-user ownership).</summary>
    public async Task<string?> CreatePlaylistAsync(string name, IEnumerable<string> songIds, CancellationToken ct = default)
        => await CreatePlaylistAsync(null, name, songIds, ct);

    /// <summary>
    /// Creates a playlist, or replaces the songs of an existing one when <paramref name="playlistId"/>
    /// is supplied (Subsonic <c>createPlaylist</c> with a playlistId replaces the tracklist).
    /// </summary>
    public async Task<string?> CreatePlaylistAsync(string? playlistId, string name, IEnumerable<string> songIds, CancellationToken ct = default)
    {
        string idPart = string.IsNullOrEmpty(playlistId)
            ? ""
            : $"playlistId={Uri.EscapeDataString(playlistId)}&";
        string extra = idPart +
            $"name={Uri.EscapeDataString(name)}" +
            string.Concat(songIds.Select(id => $"&songId={Uri.EscapeDataString(id)}"));
        SubsonicResponse? r = await CallAsync("createPlaylist", extra, ct);
        return r?.Response?.Playlist?.Id;
    }

    public async Task RenamePlaylistAsync(string playlistId, string name, CancellationToken ct = default)
    {
        string extra = $"playlistId={Uri.EscapeDataString(playlistId)}&name={Uri.EscapeDataString(name)}";
        await CallAsync("updatePlaylist", extra, ct);
    }

    public async Task AddToPlaylistAsync(string playlistId, IEnumerable<string> songIds, CancellationToken ct = default)
    {
        string extra = $"playlistId={Uri.EscapeDataString(playlistId)}" +
            string.Concat(songIds.Select(id => $"&songIdToAdd={Uri.EscapeDataString(id)}"));
        await CallAsync("updatePlaylist", extra, ct);
    }

    public async Task RemoveFromPlaylistAsync(string playlistId, int index, CancellationToken ct = default)
    {
        await CallAsync("updatePlaylist", $"playlistId={Uri.EscapeDataString(playlistId)}&songIndexToRemove={index}", ct);
    }

    public async Task DeletePlaylistAsync(string id, CancellationToken ct = default)
    {
        await CallAsync("deletePlaylist", $"id={Uri.EscapeDataString(id)}", ct);
    }

    // Playback

    /// <summary>URL to stream a song (optionally transcoding to a browser-friendly format).</summary>
    public string StreamUrl(string songId, string? format = null)
    {
        string extra = $"id={Uri.EscapeDataString(songId)}";
        if (!string.IsNullOrEmpty(format))
            extra += $"&format={format}";
        return BuildUrl("stream", extra);
    }

    public async Task ScrobbleAsync(string songId, bool submission = true, CancellationToken ct = default)
    {
        await CallAsync("scrobble", $"id={Uri.EscapeDataString(songId)}&submission={(submission ? "true" : "false")}", ct);
    }

    /// <summary>Saves the current play queue (and current song) to Navidrome so it can be resumed.</summary>
    public async Task SavePlayQueueAsync(IEnumerable<string> songIds, string? current, long positionMs, CancellationToken ct = default)
    {
        System.Text.StringBuilder sb = new();
        foreach (string id in songIds.Where(i => !string.IsNullOrWhiteSpace(i)))
            sb.Append("&id=").Append(Uri.EscapeDataString(id));
        if (!string.IsNullOrWhiteSpace(current))
            sb.Append("&current=").Append(Uri.EscapeDataString(current));
        sb.Append("&position=").Append(positionMs);
        string extra = sb.ToString().TrimStart('&');
        if (!string.IsNullOrEmpty(extra))
            await CallAsync("savePlayQueue", extra, ct);
    }

    public async Task<SubsonicPlayQueue?> GetPlayQueueAsync(CancellationToken ct = default)
    {
        SubsonicResponse? r = await CallAsync("getPlayQueue", null, ct);
        return r?.Response?.PlayQueue;
    }

    /// <summary>
    /// Reads a song's structured lyrics from Navidrome (OpenSubsonic <c>getLyricsBySongId</c>).
    /// With <paramref name="enhanced"/> this includes word-level <c>cueLine</c> timing and
    /// translation/pronunciation tracks. Returns an empty list when the song has no lyrics.
    /// </summary>
    public async Task<List<SubsonicStructuredLyrics>> GetLyricsBySongIdAsync(
        string songId, bool enhanced = true, CancellationToken ct = default)
    {
        string extra = $"id={Uri.EscapeDataString(songId)}" + (enhanced ? "&enhanced=true" : "");
        SubsonicResponse? r = await CallAsync("getLyricsBySongId", extra, ct);
        return r?.Response?.LyricsList?.StructuredLyrics ?? [];
    }

    /// <summary>Opens a streaming request, passing through a Range header for seeking.</summary>
    public async Task<HttpResponseMessage> SendStreamAsync(string songId, string? range, CancellationToken ct)
    {
        HttpRequestMessage request = new(HttpMethod.Get, StreamUrl(songId));
        if (!string.IsNullOrEmpty(range))
            request.Headers.TryAddWithoutValidation("Range", range);
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Opens a cover-art request for the given Subsonic cover-art id.</summary>
    public async Task<HttpResponseMessage> SendCoverArtAsync(string coverArtId, CancellationToken ct)
    {
        HttpRequestMessage request = new(HttpMethod.Get, BuildUrl("getCoverArt", $"id={Uri.EscapeDataString(coverArtId)}"));
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}

// DTOs

public class SubsonicResponse
{
    [JsonPropertyName("subsonic-response")] public SubsonicBody? Response { get; set; }
}

public class SubsonicBody
{
    public string Status { get; set; } = "";
    public string Version { get; set; } = "";
    public SubsonicAlbumList? AlbumList2 { get; set; }
    public SubsonicArtists? Artists { get; set; }
    public SubsonicAlbumWithSongs? Album { get; set; }
    public SubsonicPlaylists? Playlists { get; set; }
    public SubsonicPlaylist? Playlist { get; set; }
    public SubsonicSearchResult? SearchResult3 { get; set; }
    public SubsonicPlayQueue? PlayQueue { get; set; }
    public SubsonicLyricsList? LyricsList { get; set; }
}

/// <summary>OpenSubsonic <c>lyricsList</c>: the structured lyrics Navidrome holds for a song.</summary>
public class SubsonicLyricsList
{
    public List<SubsonicStructuredLyrics> StructuredLyrics { get; set; } = [];
}

/// <summary>
/// One structured lyrics track. <c>kind</c> distinguishes the main lyrics from translations and
/// pronunciations; <c>line</c> carries line start times; <c>cueLine</c> (only with
/// <c>enhanced=true</c>) carries word/syllable timing for karaoke rendering.
/// </summary>
public class SubsonicStructuredLyrics
{
    public string Kind { get; set; } = "";
    public string Lang { get; set; } = "";
    public string DisplayArtist { get; set; } = "";
    public string DisplayTitle { get; set; } = "";
    public bool Synced { get; set; }

    /// <summary>Milliseconds to shift the whole track by (some sources are consistently early or late).</summary>
    public int Offset { get; set; }

    public List<SubsonicLyricLine> Line { get; set; } = [];
    public List<SubsonicCueLine> CueLine { get; set; } = [];
    public List<SubsonicLyricAgent> Agents { get; set; } = [];
}

/// <summary>One lyric line: text plus its start time in milliseconds when synced.</summary>
public class SubsonicLyricLine
{
    public long? Start { get; set; }
    public string Value { get; set; } = "";
}

/// <summary>
/// One timed line with word-level cues. <see cref="Index"/> ties the line back to
/// <see cref="SubsonicStructuredLyrics.Line"/> at the same position.
/// </summary>
public class SubsonicCueLine
{
    public int Index { get; set; }
    public long? Start { get; set; }
    public long? End { get; set; }
    public string Value { get; set; } = "";
    public string? AgentId { get; set; }
    public List<SubsonicLyricCue> Cue { get; set; } = [];
}

/// <summary>
/// One timed word/syllable. <c>byteStart</c>/<c>byteEnd</c> are inclusive offsets into the parent
/// cueLine's UTF-8 value, so a renderer can map a cue onto its exact substring.
/// </summary>
public class SubsonicLyricCue
{
    public long? Start { get; set; }
    public long? End { get; set; }
    public string Value { get; set; } = "";
    public int? ByteStart { get; set; }
    public int? ByteEnd { get; set; }
}

/// <summary>A vocal layer a cueLine can belong to (lead, backing, a featured singer, a choir).</summary>
public class SubsonicLyricAgent
{
    public string Id { get; set; } = "";
    public string Role { get; set; } = "";
    public string Name { get; set; } = "";
}

public class SubsonicPlayQueue
{
    public List<SubsonicSong> Entry { get; set; } = [];
    public string Current { get; set; } = "";
    public long Position { get; set; }
    public string Username { get; set; } = "";
}

public class SubsonicAlbumList { public List<SubsonicAlbum> Album { get; set; } = []; }

public class SubsonicAlbum
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string ArtistId { get; set; } = "";
    public string CoverArt { get; set; } = "";
    public int SongCount { get; set; }
    public int Duration { get; set; }
    public int Year { get; set; }
    public DateTime? Created { get; set; }
    public int? PlayCount { get; set; }
}

public class SubsonicAlbumWithSongs : SubsonicAlbum
{
    public List<SubsonicSong> Song { get; set; } = [];
}

public class SubsonicArtists { public List<SubsonicIndex> Index { get; set; } = []; }
public class SubsonicIndex { public List<SubsonicArtist> Artist { get; set; } = []; }

public class SubsonicArtist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int AlbumCount { get; set; }
}

public class SubsonicSong
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Album { get; set; } = "";
    public string Artist { get; set; } = "";
    public string CoverArt { get; set; } = "";
    public int Duration { get; set; }
    public int? Track { get; set; }
}

public class SubsonicPlaylists { public List<SubsonicPlaylist> Playlist { get; set; } = []; }

public class SubsonicPlaylist
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int SongCount { get; set; }
    public int Duration { get; set; }
    public string CoverArt { get; set; } = "";

    // getPlaylist returns songs under "song"; Navidrome's OpenSubsonic returns them under "entry".
    public List<SubsonicSong> Song { get; set; } = [];
    [System.Text.Json.Serialization.JsonPropertyName("entry")]
    public List<SubsonicSong> Entry { get; set; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public List<SubsonicSong> Tracks => Song.Count > 0 ? Song : Entry;
}

public class SubsonicSearchResult
{
    public List<SubsonicSong> Song { get; set; } = [];
    public List<SubsonicAlbum> Album { get; set; } = [];
}
