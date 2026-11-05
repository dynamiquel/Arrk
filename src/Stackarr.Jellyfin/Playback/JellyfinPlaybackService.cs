using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core.Playback;

namespace Stackarr.Jellyfin.Playback;

/// <summary>
/// Turns "play this item as this user" into a concrete, playable stack: it asks Jellyfin to
/// negotiate playback with a browser-accurate device profile, then returns a Stackarr proxy URL
/// (never a Jellyfin URL) so the incoming token stays on the server.
/// </summary>
public sealed class JellyfinPlaybackService(
    JellyfinClient client,
    PlaybackSessionStore sessions,
    StreamTokenService streamTokens,
    JellyfinLinkService links,
    IOptionsMonitor<JellyfinConfig> config,
    ILogger<JellyfinPlaybackService> logger)
{
    /// <summary>Base path of the stream proxy. Kept in sync with <c>PlaybackEndpoints</c>.</summary>
    public const string StreamPathPrefix = "/api/media/player/stream";

    /// <summary>Token scope for media stream URLs (see <see cref="StreamTokenService"/>).</summary>
    public const string MediaStreamScope = "media";

    /// <summary>
    /// Lifetime of a stream token. Must outlast the longest title; sessions are ephemeral and are
    /// reaped on their own idle timeout, so an expired token is a backstop, not the primary bound.
    /// </summary>
    public static readonly TimeSpan StreamTokenLifetime = TimeSpan.FromHours(12);

    public bool IsEnabled =>
        config.CurrentValue.Enabled && config.CurrentValue.WebPlayer.Enabled;

    /// <summary>Resume position (ticks) already recorded by Jellyfin for this user/item, if any.</summary>
    public async Task<PlaybackPlan?> StartAsync(
        string username,
        string itemId,
        BrowserCapabilities capabilities,
        long? startTicks,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        CancellationToken ct)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(itemId))
            return null;

        string? userId = await client.GetUserIdByNameAsync(username, ct);
        if (userId is null)
        {
            logger.LogWarning("Web player: no Jellyfin user found for {User}", username);
            return null;
        }

        // Resume from Jellyfin's saved position unless the caller supplied a real position.
        // The player sends 0 when it has nothing to resume from, so 0 means "use Jellyfin".
        long effectiveStart = 0;
        if (startTicks is null or <= 0)
            effectiveStart = await ResolveResumeTicksAsync(userId, itemId, ct);
        else
            effectiveStart = startTicks.Value;

        // An explicit AudioStreamIndex/SubtitleStreamIndex is only honoured when Jellyfin also
        // knows which media version it belongs to. The single-item endpoint carries the sources
        // (and their streams) by default, so fetch them up front rather than asking Jellyfin to
        // negotiate without a source id and silently ignore the user's choice.
        JellyfinMediaSource? knownSource = null;
        try
        {
            JellyfinItem? item = await client.GetItemAsync(itemId, ct);
            knownSource = item?.MediaSources.FirstOrDefault();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Web player: could not load media sources for {ItemId}", itemId);
        }

        List<PlaybackAudioTrack> audioTracks = knownSource is null ? [] : ReadAudioTracks(knownSource);
        List<PlaybackSubtitle> subtitles = knownSource is null ? [] : ReadSubtitles(knownSource);

        int? selectedAudio = PickAudio(audioTracks, audioStreamIndex);
        int? selectedSubtitle = PickSubtitle(subtitles, subtitleStreamIndex);
        bool burnIn = IsBurnedIn(subtitles, selectedSubtitle);

        JellyfinDeviceProfile profile = JellyfinDeviceProfileFactory.Build(capabilities);

        JellyfinPlaybackInfoRequest request = new()
        {
            UserId = userId,
            DeviceProfile = profile,
            StartTimeTicks = effectiveStart,
            MediaSourceId = knownSource?.Id,
            AudioStreamIndex = selectedAudio,
            // Text subtitles are side-loaded as WebVTT by the player, so Jellyfin must not burn
            // them into the video. A negative index means "no subtitle" to Jellyfin.
            SubtitleStreamIndex = burnIn ? selectedSubtitle : -1,
            MaxStreamingBitrate = capabilities.MaxStreamingBitrate > 0 ? capabilities.MaxStreamingBitrate : null,
            // Browsers can't use the raw file (MKV/HEVC/AC3 …), so let Jellyfin always hand us an
            // HLS stream. It remuxes (copies) whatever it can and only re-encodes what it must.
            EnableDirectPlay = false,
            EnableDirectStream = true,
            EnableTranscoding = true,
            AllowVideoStreamCopy = true,
            AllowAudioStreamCopy = true,
            IsPlayback = true
        };

        JellyfinPlaybackInfoResponse response;
        try
        {
            response = await client.GetPlaybackInfoAsync(itemId, request, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Web player: PlaybackInfo failed for item {ItemId}", itemId);
            return null;
        }

        JellyfinMediaSource? source = response.MediaSources.FirstOrDefault();
        if (source is null)
        {
            logger.LogWarning("Web player: no media source for item {ItemId}", itemId);
            return null;
        }

        // Fallback for servers that did not return media sources on the item: the first request
        // could not carry a source id, so negotiate once more now that it is known.
        if (knownSource is null)
        {
            audioTracks = ReadAudioTracks(source);
            subtitles = ReadSubtitles(source);
            selectedAudio = PickAudio(audioTracks, audioStreamIndex);
            selectedSubtitle = PickSubtitle(subtitles, subtitleStreamIndex);
            burnIn = IsBurnedIn(subtitles, selectedSubtitle);

            request.MediaSourceId = source.Id;
            request.AudioStreamIndex = selectedAudio;
            request.SubtitleStreamIndex = burnIn ? selectedSubtitle : -1;

            try
            {
                response = await client.GetPlaybackInfoAsync(itemId, request, ct);
                source = response.MediaSources.FirstOrDefault() ?? source;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Web player: second PlaybackInfo failed for item {ItemId}", itemId);
            }
        }

        audioTracks = ReadAudioTracks(source);
        subtitles = ReadSubtitles(source);
        selectedAudio = PickAudio(audioTracks, audioStreamIndex);
        selectedSubtitle = PickSubtitle(subtitles, subtitleStreamIndex);
        burnIn = IsBurnedIn(subtitles, selectedSubtitle);

        bool isTranscode = !string.IsNullOrEmpty(source.TranscodingUrl);
        string stream = source.TranscodingUrl
            ?? source.DirectStreamUrl
            ?? $"/Videos/{itemId}/stream?static=true&MediaSourceId={source.Id}";

        PlaybackSession session = sessions.Create(username, itemId, source.Id, response.PlaySessionId);

        PlaybackPlan plan = new()
        {
            SessionId = session.SessionId,
            Protocol = "hls",
            IsTranscode = isTranscode,
            RunTimeTicks = source.RunTimeTicks ?? 0,
            StartTicks = effectiveStart,
            MediaSourceId = source.Id,
            SelectedAudioIndex = selectedAudio,
            SelectedSubtitleIndex = selectedSubtitle,
            SubtitlesBurnedIn = burnIn,
            AudioTracks = audioTracks,
            Subtitles = subtitles
        };

        // Point the browser at our proxy; strip the Jellyfin token that the URL carries. The signed
        // token sits in the *path* (not the query) so the relative URIs inside Jellyfin's HLS
        // playlists resolve back through the proxy with the token still attached, which is what
        // lets an AirPlay receiver, which has none of the browser's cookies, fetch the segments.
        string token = streamTokens.Create(MediaStreamScope, session.SessionId, StreamTokenLifetime);
        string proxyBase = $"{StreamPathPrefix}/{session.SessionId}/{token}";
        plan.StreamUrl = HlsPlaylistRewriter.StripAuthToken($"{proxyBase}{NormalizePath(stream)}");

        // Text subtitles are fetched by the browser from Jellyfin (converted to WebVTT) through
        // the same token-authenticated proxy. They are plain <track> elements, so switching them
        // needs no new transcode, only the audio needs renegotiation.
        foreach (PlaybackSubtitle subtitle in plan.Subtitles.Where(s => s.IsText))
        {
            subtitle.Url = $"{proxyBase}/Videos/{Uri.EscapeDataString(itemId)}/{Uri.EscapeDataString(source.Id)}" +
                           $"/Subtitles/{subtitle.Index}/Stream.vtt";
        }

        return plan;
    }

    /// <summary>Marks the item as finished for the user (called when the player reaches the end).</summary>
    public async Task MarkCompletedAsync(string username, string itemId, CancellationToken ct)
    {
        string? userId = await client.GetUserIdByNameAsync(username, ct);
        if (userId is null)
            return;

        try
        {
            await client.SetPlayedAsync(userId, itemId, played: true, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Web player: could not mark {ItemId} played for {User}", itemId, username);
        }
    }

    /// <summary>
    /// The next episode in the same series, used for the end-of-episode autoplay overlay.
    /// Movies, specials and the last episode return <c>null</c>.
    /// </summary>
    public async Task<PlaybackNextEpisode?> GetNextEpisodeAsync(string itemId, CancellationToken ct)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(itemId))
            return null;

        try
        {
            JellyfinItem? current = await client.GetItemAsync(itemId, ct);
            if (current is null ||
                !string.Equals(current.Type, "Episode", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(current.SeriesId))
            {
                return null;
            }

            JellyfinItemsResult response = await client.GetSeriesEpisodesAsync(current.SeriesId, ct: ct);
            List<JellyfinItem> episodes = response.Items
                .Where(episode => episode.ParentIndexNumber is > 0 && episode.IndexNumber is > 0)
                .OrderBy(episode => episode.ParentIndexNumber)
                .ThenBy(episode => episode.IndexNumber)
                .ToList();

            int index = episodes.FindIndex(episode => string.Equals(episode.Id, current.Id, StringComparison.Ordinal));
            if (index < 0 || index + 1 >= episodes.Count)
                return null;

            JellyfinItem next = episodes[index + 1];
            int season = next.ParentIndexNumber ?? 0;
            int number = next.IndexNumber ?? 0;
            string marker = $"S{season:00}E{number:00}";
            string series = next.SeriesName ?? current.SeriesName ?? "";

            return new PlaybackNextEpisode
            {
                ItemId = next.Id,
                Title = next.Name,
                Subtitle = string.IsNullOrWhiteSpace(series) ? marker : $"{series} · {marker}",
                SeasonNumber = season,
                EpisodeNumber = number,
                RunTimeTicks = next.RunTimeTicks ?? 0,
                ImageUrl = $"/api/media/player/image/{Uri.EscapeDataString(next.Id)}",
                JellyfinUrl = links.IsAvailable ? links.ItemUrl(next.Id) : ""
            };
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Web player: could not resolve the next episode for {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>Loads display metadata (title + series/season/episode line) for the player header.</summary>
    public async Task<PlaybackItemInfo?> GetItemInfoAsync(string itemId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId))
            return null;

        try
        {
            JellyfinItem? item = await client.GetItemAsync(itemId, ct);
            if (item is null)
                return null;

            PlaybackItemInfo info = BuildItemInfo(item);
            if (!info.IsMovie
                && !string.IsNullOrEmpty(item.SeriesId)
                && await client.GetItemAsync(item.SeriesId, ct) is { } series)
            {
                info.TmdbId = ProviderId(series, "Tmdb");
                info.TvdbId = ProviderId(series, "Tvdb");
                info.ImdbId = ProviderIdText(series, "Imdb");
            }

            return info;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Web player: could not load metadata for {ItemId}", itemId);
            return null;
        }
    }

    /// <summary>
    /// Builds the header lines for an item. Episodes get "Series · SxxExx" as a secondary line so
    /// the season, episode number and episode title are always visible.
    /// </summary>
    public static PlaybackItemInfo BuildItemInfo(JellyfinItem item)
    {
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            int season = item.ParentIndexNumber ?? 0;
            int episode = item.IndexNumber ?? 0;
            string marker = season > 0 || episode > 0 ? $"S{season:00}E{episode:00}" : "";

            string subtitle = string.IsNullOrWhiteSpace(item.SeriesName)
                ? marker
                : string.IsNullOrWhiteSpace(marker)
                    ? item.SeriesName!
                    : $"{item.SeriesName} · {marker}";

            return new PlaybackItemInfo
            {
                ItemId = item.Id,
                Title = item.Name,
                Subtitle = subtitle,
                IsMovie = false,
                TmdbId = ProviderId(item, "Tmdb"),
                TvdbId = ProviderId(item, "Tvdb"),
                ImdbId = ProviderIdText(item, "Imdb"),
                SeasonNumber = item.ParentIndexNumber,
                EpisodeNumber = item.IndexNumber
            };
        }

        bool isMovie = string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase);
        return new PlaybackItemInfo
        {
            ItemId = item.Id,
            Title = item.Name,
            Subtitle = "",
            IsMovie = isMovie,
            TmdbId = ProviderId(item, "Tmdb"),
            TvdbId = ProviderId(item, "Tvdb"),
            ImdbId = ProviderIdText(item, "Imdb")
        };
    }

    static int ProviderId(JellyfinItem item, string key) =>
        item.ProviderIds.TryGetValue(key, out string? value) && int.TryParse(value, out int id) ? id : 0;

    static string ProviderIdText(JellyfinItem item, string key) =>
        item.ProviderIds.TryGetValue(key, out string? value) ? value : "";

    /// <summary>Persists the user's resume position for an item.</summary>
    public async Task ReportProgressAsync(string username, string itemId, long positionTicks, CancellationToken ct)
    {
        if (positionTicks < 0)
            return;

        string? userId = await client.GetUserIdByNameAsync(username, ct);
        if (userId is null)
            return;

        try
        {
            await client.SetUserPositionAsync(userId, itemId, positionTicks, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Web player: could not save progress for {ItemId} ({User})", itemId, username);
        }
    }

    async Task<long> ResolveResumeTicksAsync(string userId, string itemId, CancellationToken ct)
    {
        try
        {
            JellyfinItem? item = await client.GetUserItemAsync(userId, itemId, ct);
            JellyfinUserItemData? data = item?.UserData;
            if (data is { Played: false, PlaybackPositionTicks: > 0 })
                return data.PlaybackPositionTicks;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Web player: could not read resume position for {ItemId}", itemId);
        }

        return 0;
    }

    /// <summary>Subtitle codecs that are pictures and therefore cannot be delivered as WebVTT.</summary>
    static readonly HashSet<string> ImageSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "pgs", "pgssub", "hdmv_pgs_subtitle",
        "dvdsub", "dvd_subtitle",
        "dvbsub", "dvb_subtitle",
        "vobsub", "xsub"
    };

    static List<PlaybackAudioTrack> ReadAudioTracks(JellyfinMediaSource source) =>
        source.MediaStreams
            .Where(s => string.Equals(s.Type, "Audio", StringComparison.OrdinalIgnoreCase))
            .Select(s => new PlaybackAudioTrack
            {
                Index = s.Index,
                DisplayTitle = s.DisplayTitle ?? "",
                Language = s.Language ?? "",
                Codec = s.Codec ?? "",
                Channels = s.Channels,
                IsDefault = s.IsDefault
            })
            .ToList();

    static List<PlaybackSubtitle> ReadSubtitles(JellyfinMediaSource source) =>
        source.MediaStreams
            .Where(s => string.Equals(s.Type, "Subtitle", StringComparison.OrdinalIgnoreCase))
            .Select(s => new PlaybackSubtitle
            {
                Index = s.Index,
                DisplayTitle = s.DisplayTitle ?? "",
                Language = s.Language ?? "",
                Codec = s.Codec ?? "",
                IsDefault = s.IsDefault,
                IsForced = s.IsForced,
                IsExternal = s.IsExternal,
                IsText = !ImageSubtitleCodecs.Contains(s.Codec ?? "")
            })
            .ToList();

    /// <summary>
    /// Resolves the audio stream to play. An explicit index wins when it exists; otherwise the
    /// default track (or the first one) is used, matching what a user expects from a player.
    /// </summary>
    static int? PickAudio(List<PlaybackAudioTrack> tracks, int? requested) =>
        requested is int index && tracks.Any(t => t.Index == index)
            ? index
            : (tracks.FirstOrDefault(t => t.IsDefault) ?? tracks.FirstOrDefault())?.Index;

    /// <summary>
    /// Resolves the subtitle stream to show. <c>null</c> means "pick the default", a negative
    /// value means "off", and an explicit index wins when it exists. Only a default text track is
    /// selected automatically so opening a title never burns subtitles in unexpectedly.
    /// </summary>
    static int? PickSubtitle(List<PlaybackSubtitle> subtitles, int? requested)
    {
        if (requested is < 0)
            return null;

        if (requested is int index)
            return subtitles.Any(s => s.Index == index) ? index : null;

        return subtitles.FirstOrDefault(s => s.IsDefault && s.IsText)?.Index;
    }

    static bool IsBurnedIn(List<PlaybackSubtitle> subtitles, int? selected) =>
        selected is int index && subtitles.FirstOrDefault(s => s.Index == index) is { IsText: false };

    static string NormalizePath(string url) => url.StartsWith('/') ? url : "/" + url;
}
