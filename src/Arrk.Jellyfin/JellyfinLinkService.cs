using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core.Configuration;
using Arrk.Tv.Abstractions;

namespace Arrk.Jellyfin;

/// <summary>
/// Builds deep links into Jellyfin's web client for the Arrk UI. Links always point at the
/// public reverse-proxy URL (e.g. <c>http://host/jellyfin</c>) rather than the internal Docker
/// address, because the internal address is not reachable from the user's browser.
/// </summary>
public class JellyfinLinkService(
    JellyfinClient client,
    IOptionsMonitor<JellyfinConfig> jellyfin,
    IOptionsMonitor<RoutesConfig> routes,
    ILogger<JellyfinLinkService> logger) : ITvPlayerLinks
{
    public const string DefaultRoutePath = "/jellyfin";

    /// <summary>True when Jellyfin is enabled and exposed through the reverse proxy.</summary>
    public bool IsAvailable => jellyfin.CurrentValue.Enabled && jellyfin.CurrentValue.Proxy;

    /// <summary>The public Jellyfin base URL (proxy base + configured route path).</summary>
    public string BaseUrl
    {
        get
        {
            RoutesConfig routeConfig = routes.CurrentValue;
            string baseUrl = routeConfig.ResolvePublicBaseUrl();

            string path = DefaultRoutePath;
            if (routeConfig.CustomRoutes.TryGetValue(JellyfinServiceDefinition.StaticKey, out string? custom) &&
                !string.IsNullOrWhiteSpace(custom))
            {
                path = custom.StartsWith('/') ? custom : "/" + custom;
            }

            return baseUrl + "/" + path.Trim('/');
        }
    }

    /// <summary>Resolves a playable Jellyfin URL for a downloaded movie, or <c>null</c> if unavailable.</summary>
    public async Task<string?> GetMovieUrlAsync(TvMovie movie, CancellationToken ct = default)
    {
        if (!IsAvailable || !movie.HasFile)
            return null;

        try
        {
            Dictionary<string, string> providers = [];
            if (movie.TmdbId > 0)
                providers["Tmdb"] = movie.TmdbId.ToString();

            string? folderName = ExtractFolderName(movie.File?.Path, "/movies");
            string? itemId = await ResolveItemIdAsync("Movie", movie.Title, providers, movie.File?.Path, folderName, ct);
            return itemId is null ? null : ItemUrl(itemId);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve Jellyfin link for movie '{Title}'", movie.Title);
            return null;
        }
    }

    /// <summary>Resolves the series link plus a map of episode deep links, or <c>null</c> if unavailable.</summary>
    public async Task<TvSeriesLinks?> GetSeriesLinksAsync(TvSeries series, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;

        try
        {
            Dictionary<string, string> providers = [];
            if (series.TvdbId > 0) providers["Tvdb"] = series.TvdbId.ToString();
            if (series.TmdbId > 0) providers["Tmdb"] = series.TmdbId.ToString();

            string? folderName = series.TvdbId > 0 ? series.TvdbId.ToString() : null;
            string? seriesId = await ResolveItemIdAsync("Series", series.Title, providers, path: null, folderName, ct);
            if (seriesId is null)
                return null;

            Dictionary<(int Season, int Episode), string> episodes = await ResolveEpisodeLinksAsync(seriesId, ct);
            return new TvSeriesLinks(ItemUrl(seriesId), episodes);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve Jellyfin link for series '{Title}'", series.Title);
            return null;
        }
    }

    /// <summary>Returns the Jellyfin URL for an episode if it was resolved, otherwise <c>null</c>.</summary>
    public static string? GetEpisodeUrl(TvSeriesLinks? links, int season, int episode) =>
        links is not null && links.Episodes.TryGetValue((season, episode), out string? url) ? url : null;

    public string ItemUrl(string itemId) => $"{BaseUrl}/web/#/details?id={itemId}";

    /// <summary>True when the in-site web player is enabled and can be used as the play target.</summary>
    public bool WebPlayerEnabled => jellyfin.CurrentValue.Enabled && jellyfin.CurrentValue.WebPlayer.Enabled;

    /// <summary>Builds an in-site player URL for a Jellyfin item id (path + optional title/back/artwork).</summary>
    public static string WatchUrl(string itemId, string? title = null, string? back = null, string? art = null)
    {
        List<string> query = [];
        if (!string.IsNullOrWhiteSpace(title))
            query.Add("title=" + Uri.EscapeDataString(title));
        if (!string.IsNullOrWhiteSpace(back))
            query.Add("back=" + Uri.EscapeDataString(back));
        if (!string.IsNullOrWhiteSpace(art))
            query.Add("art=" + Uri.EscapeDataString(art));

        string url = $"/watch/{itemId}";
        return query.Count == 0 ? url : url + "?" + string.Join('&', query);
    }

    /// <summary>
    /// Extracts the item id from a Jellyfin deep link (e.g. <c>{base}/web/#/details?id=abc</c>).
    /// Used to translate the existing Jellyfin links into in-site player links.
    /// </summary>
    public static string? ExtractItemId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        int index = url.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;

        string id = url[(index + 3)..];
        int amp = id.IndexOf('&');
        if (amp >= 0)
            id = id[..amp];

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    async Task<string?> ResolveItemIdAsync(
        string itemType,
        string title,
        Dictionary<string, string> providerIds,
        string? path,
        string? folderName,
        CancellationToken ct)
    {
        // 1. Resolve directly by provider id (tmdb.<id> / tvdb.<id>), this is what Jellyseerr
        //    does and yields a direct deep link with no search involved.
        string? id = await FindByProviderIdAsync(itemType, providerIds, ct);
        if (id is not null)
            return id;

        // 2. Search by the display title. This is the normal path once Jellyfin has TMDb metadata.
        id = await FindInSearchAsync(itemType, title, providerIds, path, ct);
        if (id is not null)
            return id;

        // 3. Arr folder names are the TMDb/TVDB id (e.g. /movies/1273221/…). Searching that
        //    still works when Jellyfin has not matched metadata yet and the item is named after
        //    the folder.
        if (!string.IsNullOrWhiteSpace(folderName) &&
            !folderName.Equals(title, StringComparison.OrdinalIgnoreCase))
        {
            id = await FindInSearchAsync(itemType, folderName, providerIds, path, ct);
            if (id is not null)
                return id;
        }

        return null;
    }

    async Task<string?> FindByProviderIdAsync(string itemType, Dictionary<string, string> providerIds, CancellationToken ct)
    {
        foreach (KeyValuePair<string, string> provider in providerIds)
        {
            if (string.IsNullOrWhiteSpace(provider.Value))
                continue;

            JellyfinItemsResult result = await client.GetItemsAsync(new JellyfinItemQuery
            {
                Recursive = true,
                IncludeItemTypes = itemType,
                AnyProviderIdEquals = $"{provider.Key.ToLowerInvariant()}.{provider.Value}",
                Fields = ["ProviderIds", "Path"],
                Limit = 5
            }, ct);

            // Jellyfin's provider filter can be loose; only trust an item that actually carries
            // the requested provider id, otherwise a query for an unknown id could return an
            // arbitrary library item and produce a wrong deep link.
            JellyfinItem? match = result.Items.FirstOrDefault(item =>
                item.ProviderIds.TryGetValue(provider.Key, out string? value) &&
                string.Equals(value, provider.Value, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                return match.Id;
        }

        return null;
    }

    async Task<string?> FindInSearchAsync(
        string itemType,
        string searchTerm,
        Dictionary<string, string> providerIds,
        string? path,
        CancellationToken ct)
    {
        JellyfinItemsResult result = await client.GetItemsAsync(new JellyfinItemQuery
        {
            Recursive = true,
            IncludeItemTypes = itemType,
            SearchTerm = searchTerm,
            Fields = ["ProviderIds", "Path"],
            Limit = 25
        }, ct);

        if (result.Items.Count == 0)
            return null;

        if (providerIds.Count > 0)
        {
            JellyfinItem? providerMatch = result.Items.FirstOrDefault(item =>
                providerIds.Any(pair =>
                    item.ProviderIds.TryGetValue(pair.Key, out string? value) &&
                    string.Equals(value, pair.Value, StringComparison.OrdinalIgnoreCase)));
            if (providerMatch is not null)
                return providerMatch.Id;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            JellyfinItem? pathMatch = result.Items.FirstOrDefault(item =>
                item.Path is not null && PathsEqual(item.Path, path));
            if (pathMatch is not null)
                return pathMatch.Id;
        }

        return result.Items.FirstOrDefault(item =>
            item.Name.Equals(searchTerm, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    async Task<Dictionary<(int Season, int Episode), string>> ResolveEpisodeLinksAsync(string seriesId, CancellationToken ct)
    {
        JellyfinItemsResult result = await client.GetSeriesEpisodesAsync(seriesId, ct: ct);
        Dictionary<(int, int), string> map = [];

        foreach (JellyfinItem episode in result.Items)
        {
            if (episode.ParentIndexNumber is null || episode.IndexNumber is null)
                continue;

            map[(episode.ParentIndexNumber.Value, episode.IndexNumber.Value)] = ItemUrl(episode.Id);
        }

        return map;
    }

    /// <summary>Extracts the first path segment below a known mount root (e.g. /movies/1273221/… → 1273221).</summary>
    public static string? ExtractFolderName(string? path, string mountRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string normalized = path.Replace('\\', '/');
        string root = mountRoot.TrimEnd('/') + "/";
        if (!normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;

        string remainder = normalized[root.Length..];
        int slash = remainder.IndexOf('/');
        return slash < 0 ? remainder : remainder[..slash];
    }

    static bool PathsEqual(string a, string b)
    {
        string na = a.Replace('\\', '/').TrimEnd('/');
        string nb = b.Replace('\\', '/').TrimEnd('/');
        return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }
    string ITvPlayerLinks.Name => "Jellyfin";

    string? ITvPlayerLinks.GetEpisodeUrl(TvSeriesLinks? links, int season, int episode) =>
        GetEpisodeUrl(links, season, episode);

    string? ITvPlayerLinks.BuildWatchUrl(string itemId, string? title, string? back, string? art) =>
        WatchUrl(itemId, title, back, art);

    string? ITvPlayerLinks.ExtractItemId(string? url) => ExtractItemId(url);
}
