using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Prowlarr.Configuration;

namespace Stackarr.Prowlarr;

public class ProwlarrService(
    ProwlarrClient client,
    IOptionsMonitor<ProwlarrConfig> config,
    StackarrInstance stackarr,
    ILogger<ProwlarrService> logger)
{
    /// <summary>The Prowlarr implementation name for the solver proxy. Byparr speaks this protocol.</summary>
    public const string FlareSolverrImplementation = "FlareSolverr";

    /// <summary>The Prowlarr config contract for the solver proxy.</summary>
    public const string FlareSolverrConfigContract = "FlareSolverrSettings";

    /// <summary>Name given to the solver proxy Stackarr manages inside Prowlarr.</summary>
    public const string SolverProxyName = "Byparr";

    /// <summary>
    /// Runs a search across all configured indexers. Music acquisition uses this with the audio
    /// categories so a title-first query actually surfaces the soundtrack album.
    /// </summary>
    public Task<List<ProwlarrSearchResult>> SearchAsync(
        string query,
        IEnumerable<int>? categories = null,
        IEnumerable<int>? indexerIds = null,
        CancellationToken ct = default)
        => client.Search(query, categories, indexerIds, ct);

    /// <summary>Ids of the enabled indexers, so a search can stream results per indexer.</summary>
    public async Task<List<int>> GetEnabledIndexerIdsAsync(CancellationToken ct = default)
    {
        try
        {
            return (await client.GetIndexers(ct))
                .Where(i => i.Enable)
                .Select(i => i.Id)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not list indexers");
            return [];
        }
    }

    /// <summary>
    /// Ensures Prowlarr has the FlareSolverr-compatible indexer proxy that points at the Byparr
    /// container, and returns the tag id that links an indexer to it. When the solver is disabled,
    /// any Stackarr-managed solver proxy is removed and <c>null</c> is returned.
    /// </summary>
    public async Task<int?> SyncIndexerSolver(CancellationToken ct = default)
    {
        SolverConfig solver = config.CurrentValue.Solver;
        List<ProwlarrIndexerProxy> proxies = await client.GetIndexerProxies(ct);
        List<ProwlarrIndexerProxy> existing = proxies
            .Where(p => p.Implementation.Equals(FlareSolverrImplementation, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!solver.Enabled)
        {
            foreach (ProwlarrIndexerProxy proxy in existing)
            {
                await client.DeleteIndexerProxy(proxy.Id, ct);
                logger.LogInformation("Deleted Cloudflare solver proxy '{Name}' (ID {Id})", proxy.Name, proxy.Id);
            }
            return null;
        }

        int tagId = await EnsureTagAsync(solver.ResolveTag(), ct);
        string host = solver.ResolveHost(BuildDefaultSolverUrl());
        int timeout = solver.ResolveRequestTimeoutSeconds();

        List<ProwlarrField> fields =
        [
            new() { Name = "host", Label = "Host", Type = "textbox", Value = host, Required = true },
            new() { Name = "requestTimeout", Label = "Request Timeout", Type = "textbox", Value = timeout }
        ];

        // Prowlarr validates the proxy on write, so it refuses to save one whose host is not yet
        // reachable (e.g. the solver container is still starting). Indexers keep their tag either
        // way and the proxy is retried on the next sync, so a transient failure must not fail the
        // whole indexer sync.
        try
        {
            ProwlarrIndexerProxy? match = existing.FirstOrDefault();
            if (match is null)
            {
                ProwlarrIndexerProxy created = await client.CreateIndexerProxy(new ProwlarrIndexerProxy
                {
                    Name = SolverProxyName,
                    Implementation = FlareSolverrImplementation,
                    ConfigContract = FlareSolverrConfigContract,
                    Fields = fields,
                    Tags = [tagId]
                }, ct);

                logger.LogInformation(
                    "Created Cloudflare solver proxy '{Name}' (ID {Id}) at {Host} (tag {Tag})",
                    created.Name, created.Id, host, tagId);

                return tagId;
            }

            if (!ProxyMatches(match, host, timeout, tagId))
            {
                match.Name = string.IsNullOrWhiteSpace(match.Name) ? SolverProxyName : match.Name;
                match.ConfigContract = FlareSolverrConfigContract;
                match.Fields = fields;
                match.Tags = [tagId];
                await client.UpdateIndexerProxy(match, ct);

                logger.LogInformation(
                    "Updated Cloudflare solver proxy '{Name}' (ID {Id}) at {Host} (tag {Tag})",
                    match.Name, match.Id, host, tagId);
            }

            // Keep exactly one solver proxy; remove any duplicates a manual edit left behind.
            foreach (ProwlarrIndexerProxy duplicate in existing.Skip(1))
            {
                await client.DeleteIndexerProxy(duplicate.Id, ct);
                logger.LogInformation("Deleted duplicate Cloudflare solver proxy '{Name}' (ID {Id})", duplicate.Name, duplicate.Id);
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                "Could not configure the Cloudflare solver proxy at {Host} (it will be retried): {Reason}",
                host, ex.Message);
        }

        return tagId;
    }

    public async Task<ProwlarrSyncResult> SyncIndexers(CancellationToken ct = default)
    {
        ProwlarrConfig prowlarrConfig = config.CurrentValue;
        List<ProwlarrIndexerDefinition> definitions = await client.GetIndexerDefinitions(ct);
        List<ProwlarrIndexer> existingIndexers = await client.GetIndexers(ct);
        List<ProwlarrAppProfile> appProfiles = await client.GetAppProfiles(ct);

        logger.LogInformation(
            "Indexer sync: {Definitions} definitions, {Existing} existing indexers, {Configured} entries configured, {Profiles} app profiles",
            definitions.Count,
            existingIndexers.Count,
            prowlarrConfig.Entries.Count,
            appProfiles.Count);

        if (prowlarrConfig.Entries.Count == 0)
        {
            logger.LogDebug("No indexer entries in config, skipping sync");
            return ProwlarrSyncResult.Empty;
        }

        // Make sure Prowlarr can reach the Cloudflare solver before wiring indexers to it.
        await SyncIndexerSolver(ct);

        int appProfileId = appProfiles.FirstOrDefault()?.Id ?? 0;
        if (appProfileId == 0)
        {
            logger.LogWarning("No app profiles found in Prowlarr, indexer creation may fail");
        }

        Dictionary<string, ProwlarrIndexerDefinition> definitionMap = BuildDefinitionMap(definitions);

        logger.LogInformation(
            "Indexer sync: built definition map with {MapCount} entries from {TotalCount} definitions",
            definitionMap.Count,
            definitions.Count);

        List<ProwlarrTag> tags = await client.GetTags(ct);

        int created = 0;
        int updated = 0;
        int unchanged = 0;

        foreach (IndexerDefinition entry in prowlarrConfig.Entries)
        {
            if (!entry.Enabled)
            {
                continue;
            }

            if (!definitionMap.TryGetValue(entry.Implementation, out ProwlarrIndexerDefinition? definition))
            {
                logger.LogWarning(
                    "Indexer implementation '{Implementation}' not found in Prowlarr schema ({Available} available), skipping '{Name}'",
                    entry.Implementation,
                    definitionMap.Count,
                    entry.Name);
                continue;
            }

            ProwlarrIndexer? existing = existingIndexers.FirstOrDefault(
                i => i.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));

            List<int> tagIds = await ResolveTagIds(entry.Tags, tags, ct);
            ProwlarrIndexer payload = BuildIndexerPayload(entry, definition, appProfileId, tagIds);

            try
            {
                if (existing != null)
                {
                    if (IndexerMatchesConfig(existing, payload))
                    {
                        unchanged++;
                        continue;
                    }

                    payload.Id = existing.Id;
                    string beforeJson = JsonSerializer.Serialize(existing, ProwlarrClient.JsonOpts);
                    string afterJson = JsonSerializer.Serialize(payload, ProwlarrClient.JsonOpts);
                    await client.UpdateIndexer(payload, ct);
                    updated++;
                    logger.LogInformation(
                        "Updated indexer '{Name}' (ID {Id}):\n  BEFORE: {Before}\n  AFTER:  {After}",
                        entry.Name, existing.Id, beforeJson, afterJson);
                }
                else
                {
                    await client.CreateIndexer(payload, ct);
                    created++;
                    logger.LogInformation("Created indexer '{Name}' ({Implementation})", entry.Name, entry.Implementation);
                }
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning(
                    "Failed to sync indexer '{Name}': {Reason}",
                    entry.Name,
                    ex.Message);
            }
        }

        HashSet<string> configuredNames = prowlarrConfig.Entries
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int deleted = 0;
        foreach (ProwlarrIndexer existing in existingIndexers)
        {
            if (!configuredNames.Contains(existing.Name))
            {
                try
                {
                    await client.DeleteIndexer(existing.Id, ct);
                    deleted++;
                    logger.LogInformation("Deleted orphaned indexer '{Name}' (ID {Id})", existing.Name, existing.Id);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning(
                        "Failed to delete orphaned indexer '{Name}' (ID {Id}): {Reason}",
                        existing.Name,
                        existing.Id,
                        ex.Message);
                }
            }
        }

        if (created > 0 || updated > 0 || unchanged > 0)
        {
            logger.LogInformation(
                "Indexer sync complete: {Created} created, {Updated} updated, {Unchanged} unchanged",
                created, updated, unchanged);
        }

        return new ProwlarrSyncResult(created, updated, unchanged, deleted);
    }

    /// <summary>
    /// Builds the lookup used to resolve a config entry's <c>implementation</c>. Cardigann
    /// definitions are addressed by their <c>definitionFile</c> (e.g. <c>1337x</c>); native
    /// implementations such as RuTracker have no definition file and are addressed by name.
    /// </summary>
    public static Dictionary<string, ProwlarrIndexerDefinition> BuildDefinitionMap(
        IEnumerable<ProwlarrIndexerDefinition> definitions)
    {
        List<ProwlarrIndexerDefinition> all = definitions.ToList();
        Dictionary<string, ProwlarrIndexerDefinition> map = new(StringComparer.OrdinalIgnoreCase);

        foreach (ProwlarrIndexerDefinition definition in all)
        {
            if (!string.IsNullOrEmpty(definition.DefinitionFile) && !map.ContainsKey(definition.DefinitionFile))
            {
                map[definition.DefinitionFile] = definition;
            }
        }

        foreach (ProwlarrIndexerDefinition definition in all)
        {
            if (!string.IsNullOrEmpty(definition.Implementation) && !map.ContainsKey(definition.Implementation))
            {
                map[definition.Implementation] = definition;
            }
        }

        return map;
    }

    /// <summary>
    /// Determines whether an indexer already stored in Prowlarr matches the desired config.
    ///
    /// Only the settings Stackarr controls are compared. Prowlarr does not echo some persisted values,
    /// such as <c>categories</c> and the <c>enableRss</c>, <c>enableAutomaticSearch</c> and
    /// <c>enableInteractiveSearch</c> flags, and it returns the full merged schema field list rather
    /// than only the fields Stackarr sent. Comparing those makes an unchanged indexer look changed on
    /// every sync.
    /// </summary>
    public static bool IndexerMatchesConfig(ProwlarrIndexer existing, ProwlarrIndexer desired)
    {
        if (existing.Enable != desired.Enable) return false;
        if (existing.Priority != desired.Priority) return false;
        if (existing.AppProfileId != desired.AppProfileId) return false;
        if (!string.Equals(existing.Name, desired.Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(existing.Implementation, desired.Implementation, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(existing.ConfigContract, desired.ConfigContract, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(existing.Protocol, desired.Protocol, StringComparison.OrdinalIgnoreCase)) return false;
        if (!existing.Tags.OrderBy(t => t).SequenceEqual(desired.Tags.OrderBy(t => t))) return false;

        // Every field we want must already be present with an equal value. Extra fields that
        // Prowlarr adds itself (schema defaults, site metadata) are ignored.
        Dictionary<string, string?> existingFields = existing.Fields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Value?.ToString(), StringComparer.OrdinalIgnoreCase);

        foreach (ProwlarrField desiredField in desired.Fields)
        {
            if (!existingFields.TryGetValue(desiredField.Name, out string? existingValue))
                return false;

            // Prowlarr masks secret fields (e.g. an indexer password) in API responses, so the
            // stored value can never be compared to what we sent. Not a change.
            if (IsMaskedSecret(existingValue))
                continue;

            string desiredValue = desiredField.Value?.ToString() ?? "";
            string actualValue = existingValue ?? "";
            if (!string.Equals(desiredValue, actualValue, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Prowlarr replaces secret field values with a run of asterisks in API responses. Such a value
    /// tells us nothing about the stored secret, so it must be treated as matching.
    /// </summary>
    static bool IsMaskedSecret(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(c => c == '*');

    static ProwlarrIndexer BuildIndexerPayload(
        IndexerDefinition entry,
        ProwlarrIndexerDefinition definition,
        int appProfileId,
        IReadOnlyList<int> tagIds)
    {
        List<ProwlarrField> fields = [];

        if (!string.IsNullOrEmpty(entry.Url))
        {
            fields.Add(new ProwlarrField
            {
                Name = "baseUrl",
                Label = "Url",
                Type = "textbox",
                Value = entry.Url,
                Required = true
            });
        }

        if (!string.IsNullOrEmpty(definition.DefinitionFile))
        {
            fields.Add(new ProwlarrField
            {
                Name = "definitionFile",
                Type = "select",
                Label = "Indexer",
                Value = definition.DefinitionFile,
                Required = true
            });
        }

        foreach (KeyValuePair<string, object> setting in entry.Settings)
        {
            fields.Add(new ProwlarrField
            {
                Name = setting.Key,
                Value = setting.Value
            });
        }

        List<string> categories = entry.Categories.Length > 0
            ? entry.Categories.ToList()
            : definition.Categories.ToList();

        return new ProwlarrIndexer
        {
            Name = entry.Name,
            Implementation = definition.Implementation,
            ConfigContract = definition.ConfigContract,
            Enable = entry.Enabled,
            Protocol = definition.Protocol,
            Priority = entry.Priority,
            AppProfileId = appProfileId,
            EnableRss = entry.Search.Search,
            EnableAutomaticSearch = entry.Search.Search,
            EnableInteractiveSearch = entry.Search.Search,
            SupportsRss = definition.SupportsRss,
            SupportsSearch = definition.SupportsSearch,
            Fields = fields,
            Categories = categories.ToArray(),
            Tags = [.. tagIds],
            DownloadClientId = 0,
            RemoveFromDownloadsAndClient = false
        };
    }

    string BuildDefaultSolverUrl() =>
        $"http://{stackarr.GetContainerName(ProwlarrServiceDefinition.SolverImplementation)}:{SolverConfig.Port}/";

    async Task<int> EnsureTagAsync(string label, CancellationToken ct)
    {
        List<ProwlarrTag> tags = await client.GetTags(ct);
        ProwlarrTag? existing = tags.FirstOrDefault(t => t.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing.Id;
        }

        ProwlarrTag created = await client.CreateTag(label, ct);
        logger.LogInformation("Created Prowlarr tag '{Label}' (ID {Id})", created.Label, created.Id);
        return created.Id;
    }

    async Task<List<int>> ResolveTagIds(string[] labels, List<ProwlarrTag> tags, CancellationToken ct)
    {
        List<int> ids = [];
        foreach (string label in labels.Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            string trimmed = label.Trim();
            ProwlarrTag? match = tags.FirstOrDefault(t => t.Label.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                match = await client.CreateTag(trimmed, ct);
                tags.Add(match);
                logger.LogInformation("Created Prowlarr tag '{Label}' (ID {Id})", match.Label, match.Id);
            }

            if (!ids.Contains(match.Id))
            {
                ids.Add(match.Id);
            }
        }

        return ids;
    }

    static bool ProxyMatches(ProwlarrIndexerProxy existing, string host, int timeout, int tagId)
    {
        string existingHost = existing.Fields
            .FirstOrDefault(f => f.Name.Equals("host", StringComparison.OrdinalIgnoreCase))?.Value?.ToString() ?? "";
        string existingTimeout = existing.Fields
            .FirstOrDefault(f => f.Name.Equals("requestTimeout", StringComparison.OrdinalIgnoreCase))?.Value?.ToString() ?? "";

        return string.Equals(existingHost.TrimEnd('/'), host.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(existingTimeout, timeout.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            && existing.Tags.Count == 1
            && existing.Tags[0] == tagId;
    }

    public static List<string> GetFilteredIndexerNames(ProwlarrConfig config, IndexerAccessConfig access)
    {
        List<string> allNames = config.Entries
            .Where(e => e.Enabled)
            .Select(e => e.Name)
            .ToList();

        if (access.Allowlist.Length > 0)
        {
            return allNames
                .Where(n => access.Allowlist.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        if (access.Blocklist.Length > 0)
        {
            return allNames
                .Where(n => !access.Blocklist.Contains(n, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        return allNames;
    }
}

/// <summary>
/// Outcome of a <see cref="ProwlarrService.SyncIndexers"/> run. Exposed so callers and tests can
/// verify that an unchanged configuration results in no writes.
/// </summary>
public readonly record struct ProwlarrSyncResult(int Created, int Updated, int Unchanged, int Deleted)
{
    public static readonly ProwlarrSyncResult Empty = new(0, 0, 0, 0);

    /// <summary>True when the sync made no changes at all.</summary>
    public bool IsNoOp => Created == 0 && Updated == 0 && Deleted == 0;
}
