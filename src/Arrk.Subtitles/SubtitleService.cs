using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Tv.Abstractions;

namespace Arrk.Subtitles;

/// <summary>
/// The subtitle dispatcher. It owns the workflow and asks every enabled provider for languages,
/// search results and downloads. Providers never schedule their own work.
/// </summary>
public sealed class SubtitleService(
    IEnumerable<ISubtitleProvider> providers,
    IOptionsMonitor<SubtitlesConfig> config,
    SubtitleStatusService status,
    SubtitleLibraryResolver library,
    ILogger<SubtitleService> logger)
{
    readonly List<ISubtitleProvider> _providers = [.. providers];
    int? _automaticBudget;

    IReadOnlyList<ISubtitleProvider> EnabledProviders =>
        config.CurrentValue.Enabled
            ? [.. _providers
                .Where(provider => provider.IsEnabled)
                .OrderBy(provider => provider.Priority)
                .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)]
            : [];

    public string ProviderName => string.Join(", ", EnabledProviders.Select(provider => provider.Name));

    public bool IsAvailable => EnabledProviders.Count > 0;

    public IReadOnlyList<string> DefaultLanguages => Preferences.Languages;

    public SubtitlePreferences Preferences => config.CurrentValue.ToPreferences();

    public void BeginAutomaticRun() => _automaticBudget = Math.Max(0, config.CurrentValue.MaxDownloadsPerRun);

    public void EndAutomaticRun() => _automaticBudget = null;

    bool TryConsumeAutomaticDownload()
    {
        if (_automaticBudget is not int budget)
            return true;

        if (budget <= 0)
            return false;

        _automaticBudget = budget - 1;
        return true;
    }

    public async Task ConfigureAsync(CancellationToken ct = default)
    {
        SubtitlePreferences preferences = Preferences;
        foreach (ISubtitleProvider provider in EnabledProviders)
        {
            try
            {
                await provider.ConfigureAsync(preferences, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not configure subtitle provider {Provider}", provider.Name);
            }
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        foreach (ISubtitleProvider provider in EnabledProviders)
        {
            try
            {
                if (await provider.IsAvailableAsync(ct))
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Subtitle provider {Provider} is not available", provider.Name);
            }
        }

        return false;
    }

    public async Task<IReadOnlyList<SubtitleLanguage>> GetLanguagesAsync(CancellationToken ct = default)
    {
        Task<IReadOnlyList<SubtitleLanguage>>[] tasks = [.. EnabledProviders
            .Select(provider => SafeLanguagesAsync(provider, ct))];
        IReadOnlyList<SubtitleLanguage>[] results = await Task.WhenAll(tasks);
        return [.. results
            .SelectMany(languages => languages)
            .Where(language => language.Code.Length > 0)
            .GroupBy(language => language.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(language => language.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public Task<SubtitleStatus?> GetStatusAsync(SubtitleTarget target, CancellationToken ct = default) =>
        status.GetStatusAsync(target, ct);

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        SubtitleTarget target,
        IReadOnlyList<string>? languages = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<string> requested = languages is null
            ? DefaultLanguages
            : languages
                .Select(SubtitleLanguages.ToTwoLetter)
                .Where(code => code.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        SubtitleSearchRequest request = new(target, requested);
        Task<IReadOnlyList<SubtitleCandidate>>[] tasks = [.. EnabledProviders
            .Select(provider => SafeSearchAsync(provider, request, ct))];
        IReadOnlyList<SubtitleCandidate>[] results = await Task.WhenAll(tasks);

        return [.. results
            .SelectMany(candidates => candidates)
            .GroupBy(candidate => (candidate.Provider, candidate.Id))
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => ProviderPriority(candidate.Provider))
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task DownloadAsync(
        SubtitleTarget target,
        SubtitleCandidate candidate,
        CancellationToken ct = default)
    {
        ISubtitleProvider? provider = _providers.FirstOrDefault(item =>
            item.IsEnabled && string.Equals(item.Name, candidate.Provider, StringComparison.OrdinalIgnoreCase))
            ?? _providers.FirstOrDefault(item =>
                string.Equals(item.Name, candidate.Provider, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
            throw new InvalidOperationException($"Subtitle provider '{candidate.Provider}' is not available.");

        await provider.DownloadAsync(new SubtitleDownloadRequest(target, candidate), ct);
    }

    public async Task RequestAsync(
        SubtitleTarget target,
        IReadOnlyList<string>? languages = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<string> requested = (languages ?? DefaultLanguages)
            .Select(SubtitleLanguages.ToTwoLetter)
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requested.Count == 0)
            return;

        switch (target.Kind)
        {
            case SubtitleMediaKind.Movie:
            case SubtitleMediaKind.Episode:
                await DownloadBestAsync(target, requested, ct);
                break;
            case SubtitleMediaKind.Series:
            case SubtitleMediaKind.Season:
            {
                List<TvEpisode> episodes = await status.GetEpisodesAsync(
                    target.SeriesId,
                    target.Kind == SubtitleMediaKind.Season ? target.SeasonNumber : null,
                    ct);

                int downloads = 0;
                foreach (TvEpisode episode in episodes)
                {
                    downloads += await DownloadBestAsync(
                        SubtitleTarget.Episode(
                            episode.SeriesId,
                            episode.Id,
                            episode.SeasonNumber,
                            episode.Title,
                            target.Subtitle),
                        requested,
                        ct,
                        remaining: Math.Max(0, config.CurrentValue.MaxDownloadsPerRun - downloads));

                    if (downloads >= config.CurrentValue.MaxDownloadsPerRun)
                        break;
                }
                break;
            }
        }
    }

    public string PreferencesFingerprint()
    {
        SubtitlePreferences preferences = Preferences;
        return $"{ProviderName}|{string.Join(',', preferences.Languages)}|{preferences.AutoDownload}";
    }

    async Task<int> DownloadBestAsync(
        SubtitleTarget target,
        IReadOnlyList<string> languages,
        CancellationToken ct,
        int? remaining = null)
    {
        int limit = remaining ?? config.CurrentValue.MaxDownloadsPerRun;
        if (limit <= 0)
            return 0;

        SubtitleStatus? current = await status.GetStatusAsync(target, ct);
        if (current is null)
            return 0;

        List<string> missing = [.. current.Missing
            .Select(language => language.Code)
            .Where(code => languages.Any(language => SubtitleLanguages.Matches(language, code)))];
        if (missing.Count == 0)
            return 0;

        IReadOnlyList<SubtitleCandidate> candidates = await SearchAsync(target, missing, ct);
        int downloaded = 0;
        foreach (string language in missing)
        {
            SubtitleCandidate? best = candidates
                .Where(candidate => SubtitleLanguages.Matches(candidate.LanguageCode, language))
                .OrderByDescending(candidate => !candidate.Forced && !candidate.HearingImpaired)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => ProviderPriority(candidate.Provider))
                .FirstOrDefault();

            if (best is null || best.Score < config.CurrentValue.MinScore)
            {
                logger.LogDebug(
                    "No subtitle match above score {Score} for {Language} {Target}",
                    config.CurrentValue.MinScore,
                    language,
                    target.Title);
                continue;
            }

            try
            {
                if (!TryConsumeAutomaticDownload())
                    break;

                await DownloadAsync(target, best, ct);
                downloaded++;
                if (downloaded >= limit)
                    break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Could not download {Language} subtitles for {Target} from {Provider}",
                    language,
                    target.Title,
                    best.Provider);
            }
        }

        return downloaded;
    }

    async Task<IReadOnlyList<SubtitleLanguage>> SafeLanguagesAsync(
        ISubtitleProvider provider,
        CancellationToken ct)
    {
        try
        {
            return await provider.GetLanguagesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Subtitle provider {Provider} could not list languages", provider.Name);
            return [];
        }
    }

    async Task<IReadOnlyList<SubtitleCandidate>> SafeSearchAsync(
        ISubtitleProvider provider,
        SubtitleSearchRequest request,
        CancellationToken ct)
    {
        try
        {
            return await provider.SearchAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Subtitle provider {Provider} could not search", provider.Name);
            return [];
        }
    }

    int ProviderPriority(string providerName) =>
        _providers.FirstOrDefault(provider =>
            string.Equals(provider.Name, providerName, StringComparison.OrdinalIgnoreCase))?.Priority ?? int.MaxValue;
}
