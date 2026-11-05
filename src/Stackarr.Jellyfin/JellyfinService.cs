using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Jellyfin;

/// <summary>
/// Drives Jellyfin from its default, unconfigured state to a fully Stackarr-managed server:
/// completes the first-run wizard, creates the administrator, sets the server name and metadata
/// language, links the Stackarr media folders as libraries using TMDb metadata, and applies the
/// Stackarr theme. Every step is idempotent so it can be re-run on a schedule or after a config
/// change without duplicating libraries or thrashing settings.
/// </summary>
public class JellyfinService(
    JellyfinClient client,
    IOptionsMonitor<JellyfinConfig> config,
    IOptionsMonitor<AuthConfig> auth,
    ILogger<JellyfinService> logger)
{
    /// <summary>Item types whose metadata should come from TMDb (the provider Stackarr uses).</summary>
    static readonly HashSet<string> TmdbItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Movie", "Series", "Season", "Episode"
    };

    /// <summary>Item types that make up each Jellyfin collection type.</summary>
    static readonly Dictionary<string, string[]> CollectionItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["movies"] = ["Movie"],
        ["tvshows"] = ["Series", "Season", "Episode"],
        ["music"] = ["MusicAlbum", "MusicArtist"],
        ["homevideos"] = ["Video"]
    };

    public const string LoginDisclaimer = "Managed by Stackarr";

    public async Task<JellyfinConfigureResult> ConfigureAsync(CancellationToken ct = default)
    {
        JellyfinConfig cfg = config.CurrentValue;
        bool startupCompleted = false;

        JellyfinPublicSystemInfo info = await client.GetPublicSystemInfoAsync(ct);
        if (!info.StartupWizardCompleted)
        {
            logger.LogInformation("Jellyfin startup wizard not completed — running first-time setup");
            await RunStartupWizardAsync(cfg, ct);
            startupCompleted = true;
        }

        // The wizard creates the administrator with the configured credentials; on an
        // already-configured server we simply sign in with them.
        await client.AuthenticateAsync(ct);

        bool serverUpdated = await EnsureServerConfigurationAsync(cfg, ct);
        bool encodingUpdated = await EnsureEncodingConfigurationAsync(cfg, ct);
        (int created, int updated) = await EnsureLibrariesAsync(cfg, ct);
        bool brandingUpdated = await EnsureBrandingAsync(cfg, ct);

        int identified = 0;
        if (cfg.ReconcileMetadata)
        {
            // A freshly created library has not been scanned yet, so wait briefly for items to
            // appear before identifying them. On later runs this is skipped.
            if (created > 0)
            {
                await client.TriggerLibraryRefreshAsync(ct);
                await WaitForLibraryItemsAsync(cfg, TimeSpan.FromSeconds(90), ct);
            }

            identified = await ReconcileMetadataAsync(cfg, ct);
        }

        logger.LogInformation(
            "Jellyfin configuration complete: startup={Startup}, serverUpdated={Server}, encodingUpdated={Encoding}, librariesCreated={Created}, librariesUpdated={Updated}, brandingUpdated={Branding}, itemsIdentified={Identified}",
            startupCompleted, serverUpdated, encodingUpdated, created, updated, brandingUpdated, identified);

        return new JellyfinConfigureResult(startupCompleted, serverUpdated, created, updated, brandingUpdated, identified);
    }

    /// <summary>Polls until at least one Movie/Series item exists in the managed libraries, or the timeout elapses.</summary>
    async Task WaitForLibraryItemsAsync(JellyfinConfig cfg, TimeSpan timeout, CancellationToken ct)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            bool hasItems = false;
            foreach (JellyfinLibraryConfig library in cfg.Libraries.Where(l => l.Enabled))
            {
                string? itemType = library.CollectionType.ToLowerInvariant() switch
                {
                    "movies" => "Movie",
                    "tvshows" => "Series",
                    _ => null
                };
                if (itemType is null)
                    continue;

                JellyfinItemsResult result = await client.GetItemsAsync(new JellyfinItemQuery
                {
                    Recursive = true,
                    IncludeItemTypes = itemType,
                    Limit = 1
                }, ct);

                if (result.Items.Count > 0)
                {
                    hasItems = true;
                    break;
                }
            }

            if (hasItems)
                return;

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    async Task RunStartupWizardAsync(JellyfinConfig cfg, CancellationToken ct)
    {
        await client.SetStartupConfigurationAsync(new JellyfinStartupConfiguration
        {
            ServerName = cfg.ServerName,
            UICulture = "en-US",
            MetadataCountryCode = cfg.MetadataCountry,
            PreferredMetadataLanguage = cfg.MetadataLanguage
        }, ct);

        // Jellyfin 12 creates the first user during initialisation and POST /Startup/User only
        // renames/sets its password (it 404s if no first user exists); Jellyfin 10 creates the
        // user on POST. Fetching the startup user first bootstraps it on 12 and is a harmless
        // no-op on 10, so the wizard works on both.
        await client.GetStartupUserAsync(ct);
        await client.SetStartupUserAsync(cfg.AdminUsername, cfg.AdminPassword, ct);
        await client.SetRemoteAccessAsync(enableRemoteAccess: true, ct);
        await client.CompleteStartupAsync(ct);

        logger.LogInformation("Jellyfin startup wizard completed; administrator '{Admin}' created", cfg.AdminUsername);
    }

    async Task<bool> EnsureServerConfigurationAsync(JellyfinConfig cfg, CancellationToken ct)
    {
        JellyfinServerConfiguration server = await client.GetServerConfigurationAsync(ct);

        bool changed = false;
        if (!string.Equals(server.ServerName, cfg.ServerName, StringComparison.Ordinal))
        {
            server.ServerName = cfg.ServerName;
            changed = true;
        }
        if (!string.Equals(server.PreferredMetadataLanguage, cfg.MetadataLanguage, StringComparison.OrdinalIgnoreCase))
        {
            server.PreferredMetadataLanguage = cfg.MetadataLanguage;
            changed = true;
        }
        if (!string.Equals(server.MetadataCountryCode, cfg.MetadataCountry, StringComparison.OrdinalIgnoreCase))
        {
            server.MetadataCountryCode = cfg.MetadataCountry;
            changed = true;
        }

        if (!changed)
            return false;

        await client.UpdateServerConfigurationAsync(server, ct);
        logger.LogInformation("Updated Jellyfin server configuration (name='{Name}', language='{Language}')",
            cfg.ServerName, cfg.MetadataLanguage);
        return true;
    }

    /// <summary>
    /// Applies NVIDIA hardware transcoding to Jellyfin's encoding configuration. The container
    /// runtime flag only exposes the GPU; Jellyfin still defaults to software encoding until this
    /// is set.
    /// </summary>
    async Task<bool> EnsureEncodingConfigurationAsync(JellyfinConfig cfg, CancellationToken ct)
    {
        if (!cfg.Nvidia)
            return false;

        JsonElement current = await client.GetEncodingConfigurationAsync(ct);
        JsonObject options = JsonNode.Parse(current.GetRawText())?.AsObject() ?? new JsonObject();
        bool changed = false;

        if (!string.Equals(options["HardwareAccelerationType"]?.GetValue<string>(), "nvenc", StringComparison.OrdinalIgnoreCase))
        {
            options["HardwareAccelerationType"] = "nvenc";
            changed = true;
        }

        if (options["EnableHardwareEncoding"]?.GetValue<bool>() != true)
        {
            options["EnableHardwareEncoding"] = true;
            changed = true;
        }

        // HDR sources tone-map on the GPU with NVENC/CUDA; leaving this off forces a software pass.
        if (options["EnableTonemapping"]?.GetValue<bool>() != true)
        {
            options["EnableTonemapping"] = true;
            changed = true;
        }

        JsonArray decodingCodecs = options["HardwareDecodingCodecs"] as JsonArray ?? [];
        foreach (string codec in new[] { "h264", "hevc", "av1" })
        {
            bool present = decodingCodecs.Any(c =>
                string.Equals(c?.GetValue<string>(), codec, StringComparison.OrdinalIgnoreCase));
            if (!present)
            {
                decodingCodecs.Add(codec);
                changed = true;
            }
        }

        options["HardwareDecodingCodecs"] = decodingCodecs;

        if (!changed)
            return false;

        await client.UpdateEncodingConfigurationAsync(options, ct);
        logger.LogInformation("Enabled NVIDIA NVENC hardware transcoding and hardware decoding in Jellyfin");
        return true;
    }

    async Task<(int Created, int Updated)> EnsureLibrariesAsync(JellyfinConfig cfg, CancellationToken ct)
    {
        List<JellyfinVirtualFolder> existing = await client.GetVirtualFoldersAsync(ct);
        int created = 0;
        int updated = 0;

        foreach (JellyfinLibraryConfig library in cfg.Libraries.Where(l => l.Enabled))
        {
            if (string.IsNullOrWhiteSpace(library.Name) || string.IsNullOrWhiteSpace(library.Path))
                continue;

            JellyfinVirtualFolder? match = FindLibrary(existing, library);

            if (match is null)
            {
                await client.AddVirtualFolderAsync(
                    library.Name, library.CollectionType, library.Path, BuildOptions(cfg, library, null), ct);
                created++;
                logger.LogInformation("Created Jellyfin library '{Name}' ({Type}) at {Path}",
                    library.Name, library.CollectionType, library.Path);
                continue;
            }

            if (!match.Locations.Contains(library.Path, StringComparer.OrdinalIgnoreCase))
            {
                await client.AddMediaPathAsync(match.Name, library.Path, ct);
                logger.LogInformation("Linked {Path} into existing Jellyfin library '{Name}'", library.Path, match.Name);
                updated++;
            }

            JellyfinLibraryOptions desired = BuildOptions(cfg, library, match.LibraryOptions);
            if (OptionsDiffer(match.LibraryOptions, desired))
            {
                await client.UpdateLibraryOptionsAsync(match.ItemId, desired, ct);
                updated++;
                logger.LogInformation("Updated Jellyfin library '{Name}' metadata options", match.Name);
            }
        }

        return (created, updated);
    }

    static JellyfinVirtualFolder? FindLibrary(List<JellyfinVirtualFolder> existing, JellyfinLibraryConfig library)
    {
        JellyfinVirtualFolder? byName = existing.FirstOrDefault(
            f => f.Name.Equals(library.Name, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
            return byName;

        // A folder at the same path is the same library even if the user renamed it.
        return existing.FirstOrDefault(f =>
            f.Locations.Any(loc => loc.Equals(library.Path, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Builds the library options Stackarr wants. When <paramref name="existing"/> is supplied the
    /// server's own values are preserved and only the managed fields are patched.
    /// </summary>
    static JellyfinLibraryOptions BuildOptions(JellyfinConfig cfg, JellyfinLibraryConfig library, JellyfinLibraryOptions? existing)
    {
        // Clone the server's options before mutating them. Returning the same instance would make
        // OptionsDiffer compare an object with itself and never detect drift.
        JellyfinLibraryOptions options = existing is null ? new JellyfinLibraryOptions() : CloneOptions(existing);

        options.Enabled = true;
        options.EnableInternetProviders = true;
        options.EnableRealtimeMonitor = true;
        options.PreferredMetadataLanguage = cfg.MetadataLanguage;
        options.MetadataCountryCode = cfg.MetadataCountry;

        if (options.PathInfos.Count == 0)
            options.PathInfos = [new() { Path = library.Path }];

        EnsureTypeOptions(options, library.CollectionType);
        return options;
    }

    static JellyfinLibraryOptions CloneOptions(JellyfinLibraryOptions source)
        => System.Text.Json.JsonSerializer.Deserialize<JellyfinLibraryOptions>(
            System.Text.Json.JsonSerializer.Serialize(source, JellyfinClient.JsonOpts),
            JellyfinClient.JsonOpts)!;

    static void EnsureTypeOptions(JellyfinLibraryOptions options, string collectionType)
    {
        if (!CollectionItemTypes.TryGetValue(collectionType, out string[]? types))
            return;

        foreach (string type in types)
        {
            JellyfinTypeOptions? typeOptions = options.TypeOptions.FirstOrDefault(
                t => t.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

            if (typeOptions is null)
            {
                typeOptions = new JellyfinTypeOptions { Type = type };
                options.TypeOptions.Add(typeOptions);
            }

            if (!TmdbItemTypes.Contains(type))
                continue;

            // Stackarr's metadata source is TMDb, so make it the only fetcher (and order).
            typeOptions.MetadataFetchers = ["TheMovieDb"];
            typeOptions.MetadataFetcherOrder = ["TheMovieDb"];
            typeOptions.ImageFetchers = ["TheMovieDb"];
            typeOptions.ImageFetcherOrder = ["TheMovieDb"];
        }
    }

    static bool OptionsDiffer(JellyfinLibraryOptions current, JellyfinLibraryOptions desired)
    {
        if (current.EnableInternetProviders != desired.EnableInternetProviders) return true;
        if (current.EnableRealtimeMonitor != desired.EnableRealtimeMonitor) return true;
        if (!string.Equals(current.PreferredMetadataLanguage, desired.PreferredMetadataLanguage, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(current.MetadataCountryCode, desired.MetadataCountryCode, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (JellyfinTypeOptions desiredType in desired.TypeOptions.Where(t => TmdbItemTypes.Contains(t.Type)))
        {
            JellyfinTypeOptions? currentType = current.TypeOptions.FirstOrDefault(
                t => t.Type.Equals(desiredType.Type, StringComparison.OrdinalIgnoreCase));
            if (currentType is null) return true;
            if (!FetchersEqual(currentType.MetadataFetchers, desiredType.MetadataFetchers)) return true;
            if (!FetchersEqual(currentType.ImageFetchers, desiredType.ImageFetchers)) return true;
        }

        return false;
    }

    static bool FetchersEqual(List<string> a, List<string> b) =>
        a.Count == b.Count && a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies library items whose folders are named by their TMDb/TVDB id (Stackarr's Radarr/
    /// Sonarr layout). Jellyfin otherwise uses the folder name as the title and cannot match
    /// metadata, leaving items showing as bare numbers. Each unidentified item is looked up by
    /// provider id and the match applied. Items that already have a provider id are skipped, so
    /// this is cheap after the first pass.
    /// </summary>
    async Task<int> ReconcileMetadataAsync(JellyfinConfig cfg, CancellationToken ct)
    {
        // Bound the work per run so a very large library cannot make thousands of metadata
        // requests in a single background pass. Matched items drop out of the query, so
        // repeated runs make progress.
        const int maxPerRun = 500;
        int identified = 0;

        foreach (JellyfinLibraryConfig library in cfg.Libraries.Where(l => l.Enabled))
        {
            if (identified >= maxPerRun)
                break;

            string? itemType = library.CollectionType.ToLowerInvariant() switch
            {
                "movies" => "Movie",
                "tvshows" => "Series",
                _ => null
            };

            if (itemType is null)
                continue;

            string providerName = itemType == "Movie" ? "Tmdb" : "Tvdb";
            JellyfinItemsResult result = await client.GetItemsAsync(new JellyfinItemQuery
            {
                Recursive = true,
                IncludeItemTypes = itemType,
                HasTmdbId = itemType == "Movie" ? false : null,
                HasTvdbId = itemType == "Series" ? false : null,
                Fields = ["ProviderIds", "Path"],
                Limit = 1000
            }, ct);

            foreach (JellyfinItem item in result.Items)
            {
                if (identified >= maxPerRun)
                    break;

                ct.ThrowIfCancellationRequested();

                string? folderId = JellyfinLinkService.ExtractFolderName(item.Path, library.Path);
                if (folderId is null || !int.TryParse(folderId, out _))
                    continue;

                try
                {
                    if (await IdentifyItemAsync(item, itemType, providerName, folderId, ct))
                        identified++;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Could not identify Jellyfin {Type} '{Name}' from id {Id}", itemType, item.Name, folderId);
                }
            }
        }

        if (identified > 0)
            logger.LogInformation("Identified {Count} Jellyfin item(s) from their TMDb/TVDB folder ids", identified);

        return identified;
    }

    async Task<bool> IdentifyItemAsync(JellyfinItem item, string itemType, string providerName, string providerId, CancellationToken ct)
    {
        List<JellyfinRemoteSearchResult> matches = await client.RemoteSearchAsync(itemType, providerName, providerId, ct);
        JellyfinRemoteSearchResult? match = matches.FirstOrDefault(m =>
            m.ProviderIds.TryGetValue(providerName, out string? value) &&
            string.Equals(value, providerId, StringComparison.OrdinalIgnoreCase))
            ?? matches.FirstOrDefault();

        if (match is null)
        {
            logger.LogDebug("No Jellyfin metadata match for {Type} id {Id}", itemType, providerId);
            return false;
        }

        await client.ApplyRemoteSearchAsync(item.Id, match, ct);
        logger.LogInformation("Identified Jellyfin {Type} '{Name}' as '{Match}' ({Provider}={Id})",
            itemType, item.Name, match.Name, providerName, providerId);
        return true;
    }

    /// <summary>
    /// Builds the login disclaimer. Jellyfin renders it as HTML on the login page but passes it
    /// through DOMPurify, which strips &lt;script&gt;, so we inject a plain SSO link (allowed)
    /// rather than the plugin's login-button script.
    /// </summary>
    string BuildLoginDisclaimer(JellyfinConfig cfg)
    {
        if (!cfg.OidcSso)
            return LoginDisclaimer;

        string basePath = new Uri(client.BaseAddress).AbsolutePath.TrimEnd('/');
        string providerId = string.IsNullOrWhiteSpace(auth.CurrentValue.Provider.JellyfinProviderId)
            ? "stackarr"
            : auth.CurrentValue.Provider.JellyfinProviderId;

        // Inline styles survive DOMPurify and outrank Jellyfin's own link-button classes.
        return
            $"<p style=\"text-align:center;margin:18px 0 6px;\">" +
            $"<a href=\"{basePath}/sso/OIDC/Start/{providerId}\" " +
            "style=\"display:block;box-sizing:border-box;max-width:320px;margin:0 auto;padding:14px 20px;" +
            "background-color:#e879a0;background:linear-gradient(135deg,#e879a0,#c084fc 55%,#818cf8);color:#ffffff !important;" +
            "font-size:1.05rem;font-weight:600;border-radius:8px;text-decoration:none;" +
            "box-shadow:0 2px 10px rgba(0,0,0,.3),0 0 12px rgba(192,132,252,.16);\">Sign in with Stackarr</a></p>" +
            $"<p style=\"text-align:center;margin:0 0 16px;\">" +
            $"<a href=\"{basePath}/sso/OIDC/QuickConnect/{providerId}\" " +
            "style=\"color:#9d97b0;font-size:0.85rem;font-weight:400;text-decoration:none;\">" +
            "Sign in a device (Quick Connect)</a></p>";
    }

    async Task<bool> EnsureBrandingAsync(JellyfinConfig cfg, CancellationToken ct)
    {
        string desiredCss = cfg.Theme
            ? (string.IsNullOrWhiteSpace(cfg.CustomCss) ? JellyfinTheme.Css : cfg.CustomCss)
            : "";

        // The OIDC plugin does not inject its login button automatically; it must be loaded via
        // Jellyfin's Login Disclaimer (rendered as HTML on the login page).
        string desiredDisclaimer = BuildLoginDisclaimer(cfg);

        JellyfinBrandingOptions branding = await client.GetBrandingAsync(ct);
        bool changed = false;

        if (!string.Equals(branding.CustomCss, desiredCss, StringComparison.Ordinal))
        {
            branding.CustomCss = desiredCss;
            changed = true;
        }
        if (!string.Equals(branding.LoginDisclaimer, desiredDisclaimer, StringComparison.Ordinal))
        {
            branding.LoginDisclaimer = desiredDisclaimer;
            changed = true;
        }

        if (!changed)
            return false;

        await client.UpdateBrandingAsync(branding, ct);
        logger.LogInformation("Applied Jellyfin branding (theme={Theme})", cfg.Theme);
        return true;
    }
}

public readonly record struct JellyfinConfigureResult(
    bool StartupCompleted,
    bool ServerUpdated,
    int LibrariesCreated,
    int LibrariesUpdated,
    bool BrandingUpdated,
    int ItemsIdentified);
