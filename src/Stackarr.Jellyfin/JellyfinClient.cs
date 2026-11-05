using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Stackarr.Jellyfin;

/// <summary>
/// Typed HTTP client for the Jellyfin REST API. Stackarr uses the administrator account
/// created during the startup wizard; the access token is cached and re-acquired lazily
/// (including on a 401, which happens after a Jellyfin restart invalidates sessions).
/// </summary>
public class JellyfinClient
{
    public static readonly JsonSerializerOptions JsonOpts = JsonSerializerOptions.Web;

    const string ClientName = "Stackarr";
    const string DeviceName = "Stackarr Server";
    const string ClientVersion = "1.0.0";

    readonly HttpClient _http;
    readonly Func<string> _baseUrl;
    readonly Func<(string Username, string Password)> _credentials;
    readonly string _deviceId;
    readonly SemaphoreSlim _authLock = new(1, 1);

    string? _token;
    string? _tokenUsername;

    public JellyfinClient(string url, string username, string password, string? deviceId = null, HttpMessageHandler? handler = null)
        : this(() => url, () => (username, password), deviceId, handler)
    {
    }

    /// <summary>
    /// Credentials-resolving constructor: the administrator credentials are resolved per
    /// authentication so editing <c>services.jellyfin.admin_username</c> / <c>admin_password</c>
    /// applies without a restart (the cached token is dropped when the username changes).
    /// </summary>
    public JellyfinClient(string url, Func<(string Username, string Password)> credentials, string? deviceId = null, HttpMessageHandler? handler = null)
        : this(() => url, credentials, deviceId, handler)
    {
    }

    /// <summary>
    /// Fully live constructor: both the base URL (which follows <c>services.jellyfin.proxy</c>, the
    /// same flag that drives Jellyfin's own BaseUrl) and the credentials are resolved per call, so
    /// editing either in the config editor applies without a restart.
    /// </summary>
    public JellyfinClient(Func<string> baseUrl, Func<(string Username, string Password)> credentials, string? deviceId = null, HttpMessageHandler? handler = null)
    {
        _baseUrl = baseUrl;
        _credentials = credentials;
        _deviceId = string.IsNullOrWhiteSpace(deviceId) ? "stackarr" : deviceId;
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(30);
        SyncBaseAddress();
    }

    /// <summary>Repoints the client at the current Jellyfin base URL (no-op when unchanged).</summary>
    void SyncBaseAddress()
    {
        string url = _baseUrl();
        if (string.IsNullOrWhiteSpace(url))
            return;

        // Trailing slash keeps any configured BaseUrl (e.g. "/jellyfin") when resolving paths.
        Uri desired = new(url.EndsWith('/') ? url : url + "/");
        if (_http.BaseAddress != desired)
            _http.BaseAddress = desired;
    }

    /// <summary>The public base address this client talks to (including any Jellyfin BaseUrl).</summary>
    public string BaseAddress
    {
        get
        {
            SyncBaseAddress();
            return _http.BaseAddress!.ToString().TrimEnd('/');
        }
    }

    public bool HasToken => _token is not null;

    /// <summary>Injects a previously obtained access token (used by the initialiser/tests).</summary>
    public void SetToken(string? token)
    {
        _token = token;
        _tokenUsername = token is null ? null : _credentials().Username;
    }

    //  Unauthenticated / startup

    public async Task<JellyfinPublicSystemInfo> GetPublicSystemInfoAsync(CancellationToken ct = default)
        => await GetJsonAsync<JellyfinPublicSystemInfo>("System/Info/Public", auth: false, ct);

    public async Task<JellyfinStartupConfiguration> GetStartupConfigurationAsync(CancellationToken ct = default)
        => await GetJsonAsync<JellyfinStartupConfiguration>("Startup/Configuration", auth: false, ct);

    public async Task SetStartupConfigurationAsync(JellyfinStartupConfiguration configuration, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "Startup/Configuration", configuration, auth: false, ct);

    /// <summary>
    /// Fetches the first startup user. Jellyfin 12 creates the first user during initialisation
    /// and this call bootstraps it (returning e.g. a generated name); Jellyfin 10 may return 404
    /// until the user is created. Calling it before <see cref="SetStartupUserAsync"/> makes the
    /// wizard work on both: on 12 the POST requires the first user to already exist.
    /// </summary>
    public async Task<JellyfinStartupUser?> GetStartupUserAsync(CancellationToken ct = default)
    {
        HttpResponseMessage response = await SendRawAsync(HttpMethod.Get, "Startup/User", body: null, auth: false, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync<JellyfinStartupUser>(response, ct);
    }

    public async Task SetStartupUserAsync(string name, string password, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "Startup/User", new JellyfinStartupUser { Name = name, Password = password }, auth: false, ct);

    public async Task SetRemoteAccessAsync(bool enableRemoteAccess, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "Startup/RemoteAccess", new { EnableRemoteAccess = enableRemoteAccess }, auth: false, ct);

    public async Task CompleteStartupAsync(CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "Startup/Complete", body: null, auth: false, ct);

    public async Task<JellyfinAuthenticationResult> AuthenticateByNameAsync(string username, string password, CancellationToken ct = default)
    {
        HttpResponseMessage response = await SendRawAsync(
            HttpMethod.Post,
            "Users/AuthenticateByName",
            new { Username = username, Pw = password },
            auth: false,
            ct);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync<JellyfinAuthenticationResult>(response, ct);
    }

    /// <summary>Authenticates with the configured credentials, caching the access token.</summary>
    public async Task<string> AuthenticateAsync(CancellationToken ct = default)
    {
        if (_token is not null
            && string.Equals(_tokenUsername, _credentials().Username, StringComparison.Ordinal))
            return _token;

        await _authLock.WaitAsync(ct);
        try
        {
            (string username, string password) = _credentials();

            // A changed admin username invalidates the cached token (it was minted for the old
            // account); a password-only change keeps working with the existing token.
            if (_token is not null && string.Equals(_tokenUsername, username, StringComparison.Ordinal))
                return _token;

            JellyfinAuthenticationResult result = await AuthenticateByNameAsync(username, password, ct);
            _token = result.AccessToken;
            _tokenUsername = username;
            return _token;
        }
        finally
        {
            _authLock.Release();
        }
    }

    //  Server configuration / branding

    public async Task<JellyfinServerConfiguration> GetServerConfigurationAsync(CancellationToken ct = default)
        => await GetJsonAsync<JellyfinServerConfiguration>("System/Configuration", auth: true, ct);

    public async Task UpdateServerConfigurationAsync(JellyfinServerConfiguration configuration, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "System/Configuration", configuration, auth: true, ct);

    public async Task<JellyfinBrandingOptions> GetBrandingAsync(CancellationToken ct = default)
        => await GetJsonAsync<JellyfinBrandingOptions>("Branding/Configuration", auth: true, ct);

    public async Task<JsonElement> GetEncodingConfigurationAsync(CancellationToken ct = default)
        => await GetJsonAsync<JsonElement>("System/Configuration/encoding", auth: true, ct);

    public async Task UpdateEncodingConfigurationAsync(object configuration, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "System/Configuration/encoding", configuration, auth: true, ct);

    public async Task UpdateBrandingAsync(JellyfinBrandingOptions branding, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "System/Configuration/Branding", branding, auth: true, ct);

    //  Libraries

    public async Task<List<JellyfinVirtualFolder>> GetVirtualFoldersAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<JellyfinVirtualFolder>>("Library/VirtualFolders", auth: true, ct);

    public async Task AddVirtualFolderAsync(string name, string collectionType, string path, JellyfinLibraryOptions options, CancellationToken ct = default)
    {
        string query = $"?name={Uri.EscapeDataString(name)}&collectionType={Uri.EscapeDataString(collectionType)}&paths={Uri.EscapeDataString(path)}&refreshLibrary=false";
        await SendAsync(HttpMethod.Post, "Library/VirtualFolders" + query, new { LibraryOptions = options }, auth: true, ct);
    }

    public async Task UpdateLibraryOptionsAsync(string itemId, JellyfinLibraryOptions options, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "Library/VirtualFolders/LibraryOptions", new { Id = itemId, LibraryOptions = options }, auth: true, ct);

    public async Task AddMediaPathAsync(string libraryName, string path, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "Library/VirtualFolders/Paths?refreshLibrary=false", new { Name = libraryName, Path = path }, auth: true, ct);

    public async Task TriggerLibraryRefreshAsync(CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "Library/Refresh", body: null, auth: true, ct);

    //  Users

    /// <summary>The username this client authenticates as (the Jellyfin administrator).</summary>
    public string Username => _credentials().Username;

    public async Task<List<JellyfinUser>> GetUsersAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<JellyfinUser>>("Users", auth: true, ct);

    /// <summary>Active sessions (with device/client names and the item each is playing).</summary>
    public async Task<List<JellyfinSession>> GetSessionsAsync(int activeWithinSeconds = 600, CancellationToken ct = default)
        => await GetJsonAsync<List<JellyfinSession>>($"Sessions?activeWithinSeconds={activeWithinSeconds}", auth: true, ct);

    public async Task<JellyfinUser> GetUserByIdAsync(string userId, CancellationToken ct = default)
        => await GetJsonAsync<JellyfinUser>($"Users/{userId}", auth: true, ct);

    public async Task<JellyfinUser> CreateUserAsync(string name, string password, CancellationToken ct = default)
    {
        HttpResponseMessage response = await SendRawAsync(
            HttpMethod.Post,
            "Users/New",
            new { Name = name, Password = password },
            auth: true,
            ct);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync<JellyfinUser>(response, ct);
    }

    /// <summary>Updates a user (name and other round-tripped fields).</summary>
    public async Task UpdateUserAsync(JellyfinUser user, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, $"Users?userId={Uri.EscapeDataString(user.Id)}", user, auth: true, ct);

    public async Task<JellyfinUser> RenameUserAsync(string userId, string newName, CancellationToken ct = default)
    {
        JellyfinUser user = await GetUserByIdAsync(userId, ct);
        user.Name = newName;
        await UpdateUserAsync(user, ct);
        return user;
    }

    /// <summary>
    /// Sets another user's password as an administrator. Jellyfin's endpoint only requires the
    /// current password when non-admins change it or an admin changes their own; an admin acting
    /// on a different user bypasses that check, so <c>CurrentPw</c> is sent empty. Note
    /// <c>ResetPassword: true</c> must NOT be used here, it clears/regenerates the password and
    /// ignores <c>NewPw</c> on Jellyfin 12.
    /// </summary>
    public async Task SetUserPasswordAsync(string userId, string newPassword, CancellationToken ct = default)
        => await SendAsync(
            HttpMethod.Post,
            $"Users/Password?userId={Uri.EscapeDataString(userId)}",
            new { CurrentPw = "", NewPw = newPassword },
            auth: true,
            ct);

    public async Task UpdateUserPolicyAsync(string userId, JellyfinUserPolicy policy, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, $"Users/{userId}/Policy", policy, auth: true, ct);

    public async Task DeleteUserAsync(string userId, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Delete, $"Users/{userId}", body: null, auth: true, ct);

    //  Plugins / packages (used to install and configure the OIDC plugin)

    public async Task<List<JellyfinRepositoryInfo>> GetRepositoriesAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<JellyfinRepositoryInfo>>("Repositories", auth: true, ct);

    public async Task SetRepositoriesAsync(IEnumerable<JellyfinRepositoryInfo> repositories, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "Repositories", repositories.ToList(), auth: true, ct);

    public async Task<List<JellyfinPackageInfo>> GetPackagesAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<JellyfinPackageInfo>>("Packages", auth: true, ct);

    public async Task InstallPackageAsync(
        string name,
        string? version = null,
        string? repositoryUrl = null,
        string? assemblyGuid = null,
        CancellationToken ct = default)
    {
        List<string> query = [];
        if (!string.IsNullOrEmpty(version))
            query.Add($"version={Uri.EscapeDataString(version)}");
        if (!string.IsNullOrEmpty(repositoryUrl))
            query.Add($"repositoryUrl={Uri.EscapeDataString(repositoryUrl)}");
        if (!string.IsNullOrEmpty(assemblyGuid))
            query.Add($"assemblyGuid={Uri.EscapeDataString(assemblyGuid)}");

        string qs = query.Count == 0 ? "" : "?" + string.Join('&', query);
        await SendAsync(HttpMethod.Post, $"Packages/Installed/{Uri.EscapeDataString(name)}{qs}", body: null, auth: true, ct);
    }

    public async Task<List<JellyfinPluginInfo>> GetPluginsAsync(CancellationToken ct = default)
        => await GetJsonAsync<List<JellyfinPluginInfo>>("Plugins", auth: true, ct);

    public async Task<JsonElement> GetPluginConfigurationAsync(string pluginId, CancellationToken ct = default)
        => await GetJsonAsync<JsonElement>($"Plugins/{Uri.EscapeDataString(pluginId)}/Configuration", auth: true, ct);

    public async Task UpdatePluginConfigurationAsync(string pluginId, object configuration, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, $"Plugins/{Uri.EscapeDataString(pluginId)}/Configuration", configuration, auth: true, ct);

    /// <summary>Restarts the Jellyfin server (required after installing/updating a plugin).</summary>
    public async Task RestartAsync(CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, "System/Restart", body: null, auth: true, ct);

    //  Items (deep-link resolution)

    public async Task<JellyfinItemsResult> GetItemsAsync(JellyfinItemQuery query, CancellationToken ct = default)
    {
        string qs = BuildItemQuery(query);
        return await GetJsonAsync<JellyfinItemsResult>("Items" + qs, auth: true, ct);
    }

    /// <summary>Items scoped to a specific user (playback state included when requested).</summary>
    public async Task<JellyfinItemsResult> GetUserItemsAsync(string userId, JellyfinItemQuery query, CancellationToken ct = default)
    {
        string qs = BuildItemQuery(query);
        return await GetJsonAsync<JellyfinItemsResult>($"Users/{userId}/Items" + qs, auth: true, ct);
    }

    /// <summary>Items a user has started but not finished (the Continue Watching list).</summary>
    public async Task<JellyfinItemsResult> GetResumeItemsAsync(string userId, int limit = 20, CancellationToken ct = default)
    {
        string qs = $"?mediaTypes={Uri.EscapeDataString("Video")}&limit={limit}" +
                    $"&fields={Uri.EscapeDataString("ProviderIds,SeriesInfo,PrimaryImageAspectRatio")}";
        return await GetJsonAsync<JellyfinItemsResult>($"Users/{userId}/Items/Resume{qs}", auth: true, ct);
    }

    /// <summary>
    /// The next unwatched episode of every series the user has started, in Jellyfin's own order.
    /// Used to keep recommending the following episode after one is finished.
    /// </summary>
    public async Task<JellyfinItemsResult> GetNextUpAsync(string userId, int limit = 20, CancellationToken ct = default)
    {
        string qs = $"?userId={Uri.EscapeDataString(userId)}&limit={limit}" +
                    $"&fields={Uri.EscapeDataString("ProviderIds,SeriesInfo")}";
        return await GetJsonAsync<JellyfinItemsResult>($"Shows/NextUp{qs}", auth: true, ct);
    }

    public async Task<JellyfinItemsResult> GetSeriesEpisodesAsync(string seriesId, int limit = 2000, CancellationToken ct = default)
    {
        string qs = $"?fields={Uri.EscapeDataString("ProviderIds,Path")}&limit={limit}";
        return await GetJsonAsync<JellyfinItemsResult>($"Shows/{seriesId}/Episodes{qs}", auth: true, ct);
    }

    //  Playback (used by the in-site web player)

    /// <summary>
    /// Asks Jellyfin to negotiate playback for an item given a device profile. Jellyfin decides
    /// direct play / direct stream (remux) / transcode and returns the playable URL.
    /// </summary>
    public async Task<Jellyfin.Playback.JellyfinPlaybackInfoResponse> GetPlaybackInfoAsync(
        string itemId,
        Jellyfin.Playback.JellyfinPlaybackInfoRequest request,
        CancellationToken ct = default)
    {
        HttpResponseMessage response = await SendRawAsync(
            HttpMethod.Post,
            $"Items/{itemId}/PlaybackInfo",
            request,
            auth: true,
            ct);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync<Jellyfin.Playback.JellyfinPlaybackInfoResponse>(response, ct);
    }

    /// <summary>Resolves a Jellyfin user id from a username, or <c>null</c> when no such user exists.</summary>
    public async Task<string?> GetUserIdByNameAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return null;

        List<JellyfinUser> users = await GetUsersAsync(ct);
        return users.FirstOrDefault(u => string.Equals(u.Name, username, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>Fetches a single item scoped to a user, including that user's watch data.</summary>
    public async Task<JellyfinItem?> GetUserItemAsync(string userId, string itemId, CancellationToken ct = default)
        => await GetJsonAsync<JellyfinItem>($"Users/{userId}/Items/{itemId}", auth: true, ct);

    /// <summary>Fetches a single item's metadata (name, series, season/episode numbers).</summary>
    public async Task<JellyfinItem?> GetItemAsync(string itemId, CancellationToken ct = default)
        => await GetJsonAsync<JellyfinItem>($"Items/{itemId}", auth: true, ct);

    /// <summary>
    /// Marks an item played/unplayed for a specific user. Unlike the progress endpoint, this one
    /// honours the user id, so an administrator token can update the correct user's watch state.
    /// </summary>
    public async Task SetPlayedAsync(string userId, string itemId, bool played, CancellationToken ct = default)
    {
        HttpMethod method = played ? HttpMethod.Post : HttpMethod.Delete;
        await SendAsync(method, $"Users/{userId}/PlayedItems/{itemId}", body: null, auth: true, ct);
    }

    /// <summary>
    /// Saves a user's playback position. An administrator token can persist resume points for any
    /// user without starting a session as that user.
    /// </summary>
    public async Task SetUserPositionAsync(string userId, string itemId, long positionTicks, CancellationToken ct = default)
        => await SendAsync(
            HttpMethod.Post,
            $"Users/{userId}/Items/{itemId}/UserData",
            new { PlaybackPositionTicks = positionTicks },
            auth: true,
            ct);

    /// <summary>
    /// Sends an authenticated request that is streamed straight through (used by the web player's
    /// stream proxy). The caller owns and must dispose the returned response.
    /// </summary>
    public async Task<HttpResponseMessage> SendProxyAsync(
        HttpMethod method,
        string path,
        string? rangeHeader,
        CancellationToken ct)
    {
        HttpResponseMessage response = await SendProxyOnceAsync(method, path, rangeHeader, ct);

        // Re-acquire the token once if Jellyfin invalidated it (e.g. a restart).
        if (response.StatusCode == HttpStatusCode.Unauthorized && _token is not null)
        {
            response.Dispose();
            SetToken(null);
            response = await SendProxyOnceAsync(method, path, rangeHeader, ct);
        }

        return response;
    }

    async Task<HttpResponseMessage> SendProxyOnceAsync(HttpMethod method, string path, string? rangeHeader, CancellationToken ct)
    {
        SyncBaseAddress();
        string token = await AuthenticateAsync(ct);
        Uri baseUri = _http.BaseAddress!;
        Uri target = new(baseUri, Relative(path));

        // Defence in depth: never follow a crafted path to a host other than Jellyfin.
        if (!string.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to proxy a request outside the Jellyfin host.");

        HttpRequestMessage request = new(method, target);
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(token));
        if (!string.IsNullOrEmpty(rangeHeader))
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Stops an active transcode job (best effort; Jellyfin also reaps idle jobs).</summary>
    public async Task DeleteActiveEncodingAsync(string? playSessionId, CancellationToken ct = default)
    {
        string qs = $"?DeviceId={Uri.EscapeDataString(_deviceId)}";
        if (!string.IsNullOrEmpty(playSessionId))
            qs += $"&PlaySessionId={Uri.EscapeDataString(playSessionId)}";

        await SendAsync(HttpMethod.Delete, "Videos/ActiveEncodings" + qs, body: null, auth: true, ct);
    }

    //  Remote metadata search (used to identify id-named folders)

    /// <summary>
    /// Searches the configured metadata providers for an item. <paramref name="itemType"/> is
    /// <c>Movie</c> or <c>Series</c>; a provider id (e.g. <c>Tmdb</c>/<c>Tvdb</c>) narrows the
    /// search to an exact match.
    /// </summary>
    public async Task<List<JellyfinRemoteSearchResult>> RemoteSearchAsync(
        string itemType,
        string providerName,
        string providerId,
        CancellationToken ct = default)
    {
        object body = new
        {
            SearchInfo = new
            {
                ProviderIds = new Dictionary<string, string> { [providerName] = providerId }
            }
        };

        HttpResponseMessage response = await SendRawAsync(
            HttpMethod.Post,
            $"Items/RemoteSearch/{itemType}",
            body,
            auth: true,
            ct);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync<List<JellyfinRemoteSearchResult>>(response, ct) ?? [];
    }

    /// <summary>Applies a remote search match to an existing library item.</summary>
    public async Task ApplyRemoteSearchAsync(string itemId, JellyfinRemoteSearchResult result, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Post, $"Items/RemoteSearch/Apply/{itemId}?replaceAllImages=true", result, auth: true, ct);

    static string BuildItemQuery(JellyfinItemQuery query)
    {
        List<string> parts = [];
        if (!string.IsNullOrWhiteSpace(query.ParentId))
            parts.Add($"parentId={Uri.EscapeDataString(query.ParentId)}");
        if (!string.IsNullOrWhiteSpace(query.IncludeItemTypes))
            parts.Add($"includeItemTypes={Uri.EscapeDataString(query.IncludeItemTypes)}");
        if (!string.IsNullOrWhiteSpace(query.MediaTypes))
            parts.Add($"mediaTypes={Uri.EscapeDataString(query.MediaTypes)}");
        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
            parts.Add($"searchTerm={Uri.EscapeDataString(query.SearchTerm)}");
        if (!string.IsNullOrWhiteSpace(query.Filters))
            parts.Add($"filters={Uri.EscapeDataString(query.Filters)}");
        if (!string.IsNullOrWhiteSpace(query.SortBy))
            parts.Add($"sortBy={Uri.EscapeDataString(query.SortBy)}");
        if (!string.IsNullOrWhiteSpace(query.SortOrder))
            parts.Add($"sortOrder={Uri.EscapeDataString(query.SortOrder)}");
        if (query.EnableUserData)
            parts.Add("enableUserData=true");
        if (query.StartIndex is int startIndex)
            parts.Add($"startIndex={startIndex}");
        if (!string.IsNullOrWhiteSpace(query.AnyProviderIdEquals))
            parts.Add($"anyProviderIdEquals={Uri.EscapeDataString(query.AnyProviderIdEquals)}");
        if (query.HasTmdbId is bool hasTmdb)
            parts.Add($"hasTmdbId={(hasTmdb ? "true" : "false")}");
        if (query.HasTvdbId is bool hasTvdb)
            parts.Add($"hasTvdbId={(hasTvdb ? "true" : "false")}");
        if (query.Recursive)
            parts.Add("recursive=true");
        if (query.Limit is > 0)
            parts.Add($"limit={query.Limit}");
        if (query.Fields.Count > 0)
            parts.Add($"fields={Uri.EscapeDataString(string.Join(',', query.Fields))}");

        return parts.Count == 0 ? "" : "?" + string.Join('&', parts);
    }

    //  Plumbing

    async Task<T> GetJsonAsync<T>(string path, bool auth, CancellationToken ct)
    {
        HttpResponseMessage response = await SendRawAsync(HttpMethod.Get, path, body: null, auth, ct);
        response.EnsureSuccessStatusCode();
        return await ReadJsonAsync<T>(response, ct);
    }

    async Task SendAsync(HttpMethod method, string path, object? body, bool auth, CancellationToken ct)
    {
        HttpResponseMessage response = await SendRawAsync(method, path, body, auth, ct);
        response.EnsureSuccessStatusCode();
    }

    async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, bool auth, CancellationToken ct)
    {
        HttpResponseMessage response = await SendOnceAsync(method, path, body, auth, ct);

        // A 401 with a cached token means the token was invalidated (e.g. Jellyfin restarted).
        // Drop it and retry exactly once with a fresh login.
        if (auth && response.StatusCode == HttpStatusCode.Unauthorized && _token is not null)
        {
            response.Dispose();
            SetToken(null);
            response = await SendOnceAsync(method, path, body, auth, ct);
        }

        return response;
    }

    async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, bool auth, CancellationToken ct)
    {
        SyncBaseAddress();

        string? token = null;
        if (auth)
            token = await AuthenticateAsync(ct);

        return await SendRawAsync(method, path, body, token, ct);
    }

    async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, string? token, CancellationToken ct)
    {
        HttpRequestMessage request = new(method, Relative(path));
        request.Headers.TryAddWithoutValidation("Authorization", BuildAuthHeader(token));
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOpts);

        return await _http.SendAsync(request, ct);
    }

    string BuildAuthHeader(string? token)
    {
        StringBuilder builder = new("MediaBrowser ");
        if (!string.IsNullOrEmpty(token))
            builder.Append($"Token=\"{token}\", ");
        builder.Append($"Client=\"{ClientName}\", Device=\"{DeviceName}\", DeviceId=\"{_deviceId}\", Version=\"{ClientVersion}\"");
        return builder.ToString();
    }

    static string Relative(string path) => path.StartsWith('/') ? path[1..] : path;

    static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        string json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOpts)!;
    }
}
