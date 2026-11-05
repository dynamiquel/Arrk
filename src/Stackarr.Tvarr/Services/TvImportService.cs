using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Events;
using Stackarr.Tvarr.Clients;
using Stackarr.Tvarr.Components.Shared;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// The local-import pipeline: ask the arr what it can see in the staging folder, build a
/// deterministic candidate list (optionally assisted by AI), then run the arr's own
/// <c>ManualImport</c> command for the title or episodes the user confirmed.
///
/// The arrs stay the library of record. Stackarr only stages the file, resolves the target and
/// reports the result; Radarr/Sonarr choose the final path, quality and metadata.
/// </summary>
public sealed class TvImportService(
    TvUploadStore store,
    RadarrClient radarrClient,
    SonarrClient sonarrClient,
    RadarrService radarr,
    SonarrService sonarr,
    MediaCatalogService catalog,
    TvImportAiMatcher aiMatcher,
    IMediaPathMapper pathMapper,
    IEventBus bus,
    ILogger<TvImportService> logger)
{
    const string ImportMode = "copy";
    static readonly TimeSpan CommandPollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    static readonly Regex SubtitleLanguage = new(
        @"\.(?<lang>[a-z]{2,3})(?:\.(?<flag>forced|sdh|cc))?\.(?<ext>srt|ass|ssa|vtt|sub|idx|sup)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<TvImportAnalysis> AnalyzeAsync(
        string batchId,
        TvImportContext context,
        CancellationToken ct = default)
    {
        List<TvUploadRecord> records = await store.GetBatchAsync(batchId, ct);
        if (records.Count == 0)
            throw new TvUploadException("No uploaded files were found for this batch.");

        string folder = store.GetContainerFolder(batchId);
        List<TvImportFileAnalysis> files = [];
        List<string> warnings = [];

        List<ManualImportResourceDto> radarrSuggestions = [];
        List<ManualImportResourceDto> sonarrSuggestions = [];

        bool wantsMovie = context.MediaType is "" or "auto" or "movie";
        bool wantsTv = context.MediaType is "" or "auto" or "tv";
        bool hasVideo = records.Any(record => TvUploadFormats.IsVideo(record.FileName));

        if (hasVideo && wantsMovie)
        {
            try
            {
                radarrSuggestions = await radarrClient.GetManualImport(folder, filterExistingFiles: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Radarr could not read the upload staging folder");
                warnings.Add("Stackarr could not read the uploaded files. You can still search for the title manually.");
            }
        }

        if (hasVideo && wantsTv)
        {
            try
            {
                sonarrSuggestions = await sonarrClient.GetManualImport(folder, filterExistingFiles: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Sonarr could not read the upload staging folder");
                warnings.Add("Stackarr could not read the uploaded files. You can still search for the title manually.");
            }
        }

        foreach (TvUploadRecord record in records)
        {
            TvParsedFile parsed = TvImportFilename.Parse(record.FileName);
            ManualImportResourceDto? suggestion = radarrSuggestions
                .Concat(sonarrSuggestions)
                .FirstOrDefault(item => string.Equals(item.Path, record.ContainerPath, StringComparison.Ordinal));

            files.Add(new TvImportFileAnalysis(
                record.Id,
                record.FileName,
                record.SizeBytes,
                TvUploadFormats.IsVideo(record.FileName),
                TvUploadFormats.IsSubtitle(record.FileName),
                parsed,
                suggestion?.Movie?.Title ?? suggestion?.Series?.Title,
                suggestion?.Quality?.Quality?.Name,
                suggestion?.Rejections.Select(rejection => rejection.Reason).ToList() ?? []));
        }

        TvParsedFile parsedFile = files.FirstOrDefault(file => file.IsVideo)?.Parsed
            ?? files.FirstOrDefault()?.Parsed
            ?? TvImportFilename.Parse("upload.mkv");

        if (!hasVideo)
            warnings.Add("No video file was uploaded. Add a movie or episode file to import.");

        TvImportCandidate? target = null;
        if (!context.AutoDetect)
        {
            target = await ResolveKnownTargetAsync(context, ct);
            if (target is null || string.IsNullOrWhiteSpace(target.Title))
                throw new TvUploadException("Could not resolve that title. Search for it manually first.");
        }

        IReadOnlyList<TvImportCandidate> candidates;
        if (target is not null)
        {
            candidates = [target];
        }
        else
        {
            List<TvImportCandidate> combined = [];

            if (hasVideo && wantsMovie)
                combined.AddRange(await AnalyzeMoviesAsync(parsedFile, radarrSuggestions, ct));

            if (hasVideo && wantsTv)
                combined.AddRange(await AnalyzeSeriesAsync(parsedFile, sonarrSuggestions, ct));

            candidates = Merge(combined);

            if (candidates.Count == 0 && hasVideo)
                warnings.Add("Stackarr could not identify this file. Search for the movie or show manually.");
            else if (candidates.Count > 0 && candidates[0].Score < TvImportMatcher.AutoPickThreshold)
                warnings.Add("The best match is low confidence. Check it before importing.");
        }

        TvImportAiDecision? ai = null;
        TvImportCandidate? match = target;

        if (target is null && hasVideo)
        {
            ai = await aiMatcher.PickAsync(parsedFile, candidates, ct);
            if (ai is { Applied: true } && ai.Pick > 0 && ai.Pick <= candidates.Count)
                match = candidates[ai.Pick - 1];
            else
                match = candidates.FirstOrDefault();
        }

        return new TvImportAnalysis(batchId, files, candidates, match, ai, warnings);
    }

    public async Task<TvImportResult> ImportAsync(TvImportRequest request, CancellationToken ct = default)
    {
        List<TvUploadRecord> records = await store.GetBatchAsync(request.BatchId, ct);
        if (records.Count == 0)
            throw new TvUploadException("No uploaded files were found for this batch.");

        bool isTv = string.Equals(request.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
            || (request.TvdbId > 0 && request.TmdbId == 0);

        try
        {
            return isTv
                ? await ImportEpisodesAsync(request, records, ct)
                : await ImportMovieAsync(request, records, ct);
        }
        catch (TvUploadException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Local import failed for batch {Batch}", request.BatchId);
            await MarkFailedAsync(records, ex.Message, ct);
            return new TvImportResult(
                request.BatchId,
                false,
                $"Import failed: {ex.Message}",
                0,
                0,
                records.Select(record => new TvImportFileResult(
                    record.Id,
                    record.FileName,
                    TvUploadStatus.Failed,
                    ex.Message,
                    record.SeasonNumber,
                    record.EpisodeNumber,
                    "unknown")).ToList());
        }
    }

    //  Analysis

    async Task<TvImportCandidate?> ResolveKnownTargetAsync(TvImportContext context, CancellationToken ct)
    {
        if (string.Equals(context.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            TvSeries? series = context.LibraryId > 0
                ? await catalog.GetLibrarySeriesAsync(context.TmdbId, ct)
                : null;

            if (context.LibraryId > 0 && series is null)
            {
                try
                {
                    SeriesDto? dto = await sonarrClient.GetSeries(context.LibraryId, ct);
                    if (dto is not null)
                    {
                        series = new TvSeries
                        {
                            Id = dto.Id,
                            Title = dto.Title,
                            Year = dto.Year,
                            TmdbId = dto.TmdbId,
                            TvdbId = dto.TvdbId,
                            PosterUrl = dto.Images.FirstOrDefault(image => image.CoverType == "poster")?.RemoteUrl ?? ""
                        };
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogDebug(ex, "Could not load the target series {SeriesId}", context.LibraryId);
                }
            }

            return TvImportMatcher.ManualCandidate(
                "tv",
                series?.Title ?? context.Title,
                series?.Year ?? context.Year,
                series?.TmdbId ?? context.TmdbId,
                series?.TvdbId ?? context.TvdbId,
                "",
                series?.PosterUrl ?? context.PosterUrl,
                series?.Id ?? (context.LibraryId > 0 ? context.LibraryId : null),
                []);
        }

        TvMovie? movie = null;
        if (context.LibraryId > 0)
        {
            try
            {
                movie = await radarr.GetMovie(context.LibraryId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Could not load the target movie {MovieId}", context.LibraryId);
            }
        }

        if (movie is null && context.TmdbId > 0)
            movie = await catalog.GetLibraryMovieAsync(context.TmdbId, ct);

        return TvImportMatcher.ManualCandidate(
            "movie",
            movie?.Title ?? context.Title,
            movie?.Year ?? context.Year,
            movie?.TmdbId ?? context.TmdbId,
            0,
            movie?.Overview ?? "",
            movie?.PosterUrl ?? context.PosterUrl,
            movie?.Id ?? (context.LibraryId > 0 ? context.LibraryId : null),
            []);
    }

    async Task<IReadOnlyList<TvImportCandidate>> AnalyzeMoviesAsync(
        TvParsedFile parsed,
        IReadOnlyList<ManualImportResourceDto> suggestions,
        CancellationToken ct)
    {
        List<MovieLookupDto> lookups = [];
        Dictionary<int, TvMovie> library = [];

        try
        {
            foreach (TvMovie movie in await radarr.GetAllMovies(ct))
            {
                if (movie.TmdbId > 0)
                    library[movie.TmdbId] = movie;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the movie library during analysis");
        }

        foreach (ManualImportResourceDto suggestion in suggestions)
        {
            if (suggestion.Movie is { TmdbId: > 0 } movie)
                lookups.Add(ToLookup(movie));
        }

        if (lookups.Count == 0 && parsed.CleanTitle.Length > 0)
        {
            try
            {
                lookups.AddRange(await radarrClient.SearchMovie(parsed.CleanTitle, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Radarr lookup failed during local-import analysis");
            }
        }

        HashSet<int> parsedMovieIds = [.. suggestions
            .Where(item => item.Movie is { TmdbId: > 0 })
            .Select(item => item.Movie!.TmdbId)];

        List<TvImportCandidate> candidates = [.. TvImportMatcher.RankMovies(parsed, lookups, library)
            .Select(candidate => parsedMovieIds.Contains(candidate.TmdbId)
                ? candidate with
                {
                    Score = Math.Min(100, candidate.Score + 12),
                    Reasons = [.. candidate.Reasons, "Matched this file"]
                }
                : candidate)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.InLibrary)
            .ThenBy(candidate => candidate.Title)];

        if (candidates.Count == 0 && catalog.IsTmdbConfigured && parsed.CleanTitle.Length > 0)
        {
            try
            {
                List<MediaItem> results = await catalog.SearchMoviesAsync(parsed.CleanTitle, 1, ct);
                candidates.AddRange(results.Take(8).Select(item => FromMediaItem(parsed, item, "movie")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "TMDB movie search failed during local-import analysis");
            }
        }

        return candidates;
    }

    async Task<IReadOnlyList<TvImportCandidate>> AnalyzeSeriesAsync(
        TvParsedFile parsed,
        IReadOnlyList<ManualImportResourceDto> suggestions,
        CancellationToken ct)
    {
        List<SeriesLookupDto> lookups = [];
        Dictionary<int, TvSeries> libraryByTmdb = [];
        Dictionary<int, TvSeries> libraryByTvdb = [];

        try
        {
            foreach (TvSeries series in await sonarr.GetAllSeries(ct))
            {
                if (series.TmdbId > 0)
                    libraryByTmdb[series.TmdbId] = series;
                if (series.TvdbId > 0)
                    libraryByTvdb[series.TvdbId] = series;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the show library during analysis");
        }

        foreach (ManualImportResourceDto suggestion in suggestions)
        {
            if (suggestion.Series is { TvdbId: > 0 } series)
                lookups.Add(ToLookup(series));
        }

        if (lookups.Count == 0 && parsed.CleanTitle.Length > 0)
        {
            try
            {
                lookups.AddRange(await sonarrClient.SearchSeries(parsed.CleanTitle, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Sonarr lookup failed during local-import analysis");
            }
        }

        HashSet<int> parsedSeriesIds = [.. suggestions
            .Where(item => item.Series is { TvdbId: > 0 })
            .Select(item => item.Series!.TvdbId)];

        List<TvImportCandidate> candidates =
            [.. TvImportMatcher.RankSeries(parsed, lookups, libraryByTmdb, libraryByTvdb)
                .Select(candidate => parsedSeriesIds.Contains(candidate.TvdbId)
                    ? candidate with
                    {
                        Score = Math.Min(100, candidate.Score + 12),
                        Reasons = [.. candidate.Reasons, "Matched this file"]
                    }
                    : candidate)
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.InLibrary)
                .ThenBy(candidate => candidate.Title)];

        if (candidates.Count == 0 && catalog.IsTmdbConfigured && parsed.CleanTitle.Length > 0)
        {
            try
            {
                List<MediaItem> results = await catalog.SearchShowsAsync(parsed.CleanTitle, 1, ct);
                candidates.AddRange(results.Take(8).Select(item => FromMediaItem(parsed, item, "tv")));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "TMDB show search failed during local-import analysis");
            }
        }

        return candidates;
    }

    static MovieLookupDto ToLookup(MovieDto movie) => new()
    {
        Title = movie.Title,
        OriginalTitle = movie.OriginalTitle,
        Year = movie.Year,
        Overview = movie.Overview,
        Ratings = movie.Ratings,
        Genres = movie.Genres,
        ImdbId = movie.ImdbId,
        TmdbId = movie.TmdbId,
        Images = movie.Images,
        Runtime = movie.Runtime,
        Status = movie.Status,
        Certification = movie.Certification,
        Studio = movie.Studio,
        OriginalLanguage = movie.OriginalLanguage,
        InCinemas = movie.InCinemas,
        PhysicalRelease = movie.PhysicalRelease,
        DigitalRelease = movie.DigitalRelease,
        ReleaseDate = movie.ReleaseDate,
        YouTubeTrailerId = movie.YouTubeTrailerId,
        Collection = movie.Collection
    };

    static SeriesLookupDto ToLookup(SeriesDto series) => new()
    {
        Title = series.Title,
        OriginalTitle = series.OriginalTitle,
        Year = series.Year,
        Overview = series.Overview,
        Ratings = series.Ratings,
        Genres = series.Genres,
        TvdbId = series.TvdbId,
        TmdbId = series.TmdbId,
        ImdbId = series.ImdbId,
        Images = series.Images,
        Runtime = series.Runtime,
        Status = series.Status,
        Network = series.Network,
        Certification = series.Certification,
        SeriesType = series.SeriesType,
        FirstAired = series.FirstAired,
        Seasons = series.Seasons,
        OriginalLanguage = series.OriginalLanguage
    };

    static TvImportCandidate FromMediaItem(TvParsedFile parsed, MediaItem item, string mediaType)
    {
        List<string> reasons = ["TMDB search result"];
        double score = TvImportMatcher.Score(parsed, item.Title, item.OriginalTitle, item.Year, reasons, fromArr: false);
        return new TvImportCandidate(
            mediaType,
            item.Title,
            item.OriginalTitle,
            item.Year,
            item.TmdbId,
            item.TvdbId,
            item.Overview,
            item.PosterUrl,
            score,
            reasons,
            item.IsInLibrary ? item.Id : null,
            item.IsInLibrary,
            item.AvailableSeasonNumbers ?? [],
            FromArr: false);
    }

    static IReadOnlyList<TvImportCandidate> Merge(IEnumerable<TvImportCandidate> candidates)
    {
        return [.. candidates
            .GroupBy(candidate => candidate.MediaType == "movie"
                ? $"movie:{candidate.TmdbId}"
                : $"tv:{candidate.TvdbId}:{candidate.TmdbId}")
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.InLibrary)
            .ThenBy(candidate => candidate.Title)
            .Take(20)];
    }

    //  Movie import

    async Task<TvImportResult> ImportMovieAsync(
        TvImportRequest request,
        List<TvUploadRecord> records,
        CancellationToken ct)
    {
        List<TvUploadRecord> videos = [.. records.Where(record => TvUploadFormats.IsVideo(record.FileName))];
        List<TvUploadRecord> subtitles = [.. records.Where(record => TvUploadFormats.IsSubtitle(record.FileName))];

        if (videos.Count == 0)
            throw new TvUploadException("Add one video file to import a movie.");
        if (videos.Count > 1)
            throw new TvUploadException("A movie upload takes exactly one video file.");

        TvUploadRecord video = videos[0];
        MovieDto movie = await EnsureMovieAsync(request, ct);

        if (movie.HasFile && !request.ReplaceExisting)
            throw new TvUploadException(
                $"{movie.Title} already has a file. Enable \"Replace existing file\" to upload over it.");

        List<ManualImportResourceDto> suggestions = await SafeGetRadarrSuggestionsAsync(request.BatchId, ct);
        ManualImportResourceDto suggestion = suggestions.FirstOrDefault(
            item => string.Equals(item.Path, video.ContainerPath, StringComparison.Ordinal)) ?? new();

        ManualImportResourceDto reprocessed = await ReprocessMovieAsync(video, movie.Id, ct) ?? suggestion;

        if (request.ReplaceExisting && movie.HasFile)
            await DeleteMovieFilesAsync(movie.Id, ct);

        ManualImportFileDto file = new()
        {
            Path = video.ContainerPath,
            MovieId = movie.Id,
            Quality = reprocessed.Quality ?? suggestion.Quality,
            Languages = reprocessed.Languages.Count > 0 ? reprocessed.Languages : suggestion.Languages,
            ReleaseGroup = reprocessed.ReleaseGroup ?? suggestion.ReleaseGroup,
            IndexerFlags = reprocessed.IndexerFlags
        };

        video.Status = TvUploadStatus.Importing;
        video.LibraryId = movie.Id;
        await store.UpdateAsync(video, ct);

        ArrCommandDto command = await radarrClient.SendManualImport([file], ImportMode, ct);
        CommandOutcome outcome = await WaitForCommandAsync(radarrClient, command, ct);

        if (!outcome.Success)
        {
            await MarkFailedAsync(records, outcome.Message, ct);
            return new TvImportResult(
                request.BatchId,
                false,
                outcome.Message,
                movie.Id,
                0,
                [new TvImportFileResult(video.Id, video.FileName, TvUploadStatus.Failed, outcome.Message, 0, 0, file.Quality?.Quality?.Name ?? "unknown")]);
        }

        MovieDto refreshed = await radarrClient.GetMovie(movie.Id, ct) ?? movie;
        string? importedPath = refreshed.MovieFile?.Path
            ?? (await radarrClient.GetMovieFiles(movie.Id, ct)).FirstOrDefault()?.Path;
        string? localMoviePath = importedPath is null ? null : pathMapper.MapToLocalPath(importedPath) ?? importedPath;

        List<TvImportFileResult> results =
        [
            new(video.Id, video.FileName, TvUploadStatus.Imported, "Imported into the movie library", 0, 0, file.Quality?.Quality?.Name ?? "unknown")
        ];

        if (request.ImportSubtitles && subtitles.Count > 0 && localMoviePath is not null)
        {
            foreach (TvUploadRecord subtitle in subtitles)
                results.Add(CopySubtitle(subtitle, localMoviePath));
        }
        else
        {
            results.AddRange(subtitles.Select(record => new TvImportFileResult(
                record.Id, record.FileName, TvUploadStatus.Failed, "No imported video to attach this subtitle to", 0, 0, "")));
        }

        video.Status = TvUploadStatus.Imported;
        await store.UpdateAsync(video, ct);

        await FinishAsync(records, results, ct);

        if (refreshed.HasFile)
            await bus.Publish(new MediaImportedEvent(
                "local-upload",
                refreshed.Title,
                "Movie",
                MovieId: refreshed.Id), ct);

        return new TvImportResult(
            request.BatchId,
            refreshed.HasFile,
            refreshed.HasFile
                ? $"Imported {refreshed.Title} into your movie library."
                : "The movie file was not found after the import.",
            movie.Id,
            0,
            results);
    }

    async Task<MovieDto> EnsureMovieAsync(TvImportRequest request, CancellationToken ct)
    {
        MovieDto? movie = null;

        if (request.LibraryId > 0)
            movie = await radarrClient.GetMovie(request.LibraryId, ct);

        if (movie is null && request.TmdbId > 0)
        {
            List<MovieDto> movies = await radarrClient.GetMovies(ct);
            movie = movies.FirstOrDefault(candidate => candidate.TmdbId == request.TmdbId);
        }

        if (movie is not null)
            return movie;

        if (request.TmdbId <= 0)
            throw new TvUploadException("Could not resolve the movie to add. Search for it manually first.");

        return await radarr.AddMovieForImportAsync(request.TmdbId, request.QualityProfileId, ct);
    }

    async Task DeleteMovieFilesAsync(int movieId, CancellationToken ct)
    {
        List<MovieFileDto> files = await radarrClient.GetMovieFiles(movieId, ct);
        foreach (MovieFileDto file in files)
            await radarrClient.DeleteMovieFile(file.Id, ct);

        if (files.Count > 0)
            logger.LogInformation("Removed {Count} existing movie file(s) before a local import", files.Count);
    }

    async Task<ManualImportResourceDto?> ReprocessMovieAsync(
        TvUploadRecord video,
        int movieId,
        CancellationToken ct)
    {
        try
        {
            List<ManualImportResourceDto> items = await radarrClient.ReprocessManualImport(
                [new { path = video.ContainerPath, movieId }],
                ct);
            return items.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Radarr could not reprocess the uploaded movie file");
            return null;
        }
    }

    async Task<List<ManualImportResourceDto>> SafeGetRadarrSuggestionsAsync(string batchId, CancellationToken ct)
    {
        try
        {
            return await radarrClient.GetManualImport(store.GetContainerFolder(batchId), filterExistingFiles: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Radarr could not read the upload staging folder");
            return [];
        }
    }

    //  Episode import

    async Task<TvImportResult> ImportEpisodesAsync(
        TvImportRequest request,
        List<TvUploadRecord> records,
        CancellationToken ct)
    {
        List<TvUploadRecord> videos = [.. records.Where(record => TvUploadFormats.IsVideo(record.FileName))];
        List<TvUploadRecord> subtitles = [.. records.Where(record => TvUploadFormats.IsSubtitle(record.FileName))];

        if (videos.Count == 0)
            throw new TvUploadException("Add at least one episode video file to import.");

        SeriesDto series = await EnsureSeriesAsync(request, ct);
        List<EpisodeDto> episodes = await sonarrClient.GetEpisodes(series.Id, ct);
        Dictionary<(int Season, int Episode), EpisodeDto> byNumber = episodes
            .GroupBy(episode => (episode.SeasonNumber, episode.EpisodeNumber))
            .ToDictionary(group => group.Key, group => group.First());
        Dictionary<int, EpisodeDto> byId = episodes
            .GroupBy(episode => episode.Id)
            .ToDictionary(group => group.Key, group => group.First());

        List<ManualImportResourceDto> suggestions = await SafeGetSonarrSuggestionsAsync(request.BatchId, ct);
        Dictionary<string, ManualImportResourceDto> suggestionByPath = suggestions
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .GroupBy(item => item.Path)
            .ToDictionary(group => group.Key, group => group.First());

        List<ManualImportFileDto> files = [];
        Dictionary<long, List<EpisodeDto>> mappedByUpload = [];
        HashSet<int> claimedEpisodes = [];
        HashSet<int> deletedFileIds = [];
        List<string> failures = [];

        foreach (TvUploadRecord video in videos)
        {
            List<EpisodeDto> mapped = ResolveEpisodes(
                video,
                request,
                series.Id,
                byNumber,
                byId,
                suggestionByPath,
                claimedEpisodes,
                out string? failure);

            if (mapped.Count == 0)
            {
                failures.Add($"{video.FileName}: {failure}");
                video.Status = TvUploadStatus.Failed;
                video.Error = failure;
                await store.UpdateAsync(video, ct);
                continue;
            }

            foreach (EpisodeDto episode in mapped)
                claimedEpisodes.Add(episode.Id);

            ManualImportResourceDto suggestion = suggestionByPath.GetValueOrDefault(video.ContainerPath) ?? new();
            ManualImportResourceDto reprocessed = await ReprocessEpisodeAsync(video, series.Id, mapped, ct) ?? suggestion;

            List<string> rejections = reprocessed.Rejections.Count > 0
                ? reprocessed.Rejections.Select(rejection => rejection.Reason).ToList()
                : suggestion.Rejections.Select(rejection => rejection.Reason).ToList();

            bool hasFile = mapped.Any(episode => episode.HasFile);
            if (hasFile && !request.ReplaceExisting)
            {
                string message = rejections.Count > 0
                    ? string.Join("; ", rejections)
                    : "This episode already has a file. Enable \"Replace existing file\" to upload over it.";
                failures.Add($"{video.FileName}: {message}");
                video.Status = TvUploadStatus.Failed;
                video.Error = message;
                await store.UpdateAsync(video, ct);
                continue;
            }

            if (hasFile && request.ReplaceExisting)
            {
                HashSet<int> fileIds = [.. mapped
                    .Where(episode => episode.HasFile && episode.EpisodeFileId > 0)
                    .Select(episode => episode.EpisodeFileId)];

                foreach (int fileId in fileIds.Where(deletedFileIds.Add))
                    await sonarrClient.DeleteEpisodeFile(fileId, ct);
            }

            files.Add(new ManualImportFileDto
            {
                Path = video.ContainerPath,
                SeriesId = series.Id,
                SeasonNumber = mapped[0].SeasonNumber,
                EpisodeIds = mapped.Select(episode => episode.Id).ToList(),
                Quality = reprocessed.Quality ?? suggestion.Quality,
                Languages = reprocessed.Languages.Count > 0 ? reprocessed.Languages : suggestion.Languages,
                ReleaseGroup = reprocessed.ReleaseGroup ?? suggestion.ReleaseGroup,
                ReleaseType = mapped.Count > 1 ? "multiEpisode" : "singleEpisode",
                IndexerFlags = reprocessed.IndexerFlags
            });

            mappedByUpload[video.Id] = mapped;
            video.Status = TvUploadStatus.Importing;
            video.LibraryId = series.Id;
            video.SeasonNumber = mapped[0].SeasonNumber;
            video.EpisodeNumber = mapped[0].EpisodeNumber;
            await store.UpdateAsync(video, ct);
        }

        if (files.Count == 0)
        {
            string message = failures.Count > 0 ? string.Join(" ", failures) : "No episode file could be matched.";
            List<TvImportFileResult> noFiles = records.Select(record => new TvImportFileResult(
                record.Id,
                record.FileName,
                TvUploadStatus.Failed,
                record.Error ?? message,
                record.SeasonNumber,
                record.EpisodeNumber,
                "unknown")).ToList();

            await FinishAsync(records, noFiles, ct);
            return new TvImportResult(request.BatchId, false, message, 0, series.Id, noFiles);
        }

        ArrCommandDto command = await sonarrClient.SendManualImport(files, ImportMode, ct);
        CommandOutcome outcome = await WaitForCommandAsync(sonarrClient, command, ct);

        if (!outcome.Success)
        {
            await MarkFailedAsync(records, outcome.Message, ct);
            return new TvImportResult(
                request.BatchId,
                false,
                outcome.Message,
                0,
                series.Id,
                files.Select(file => new TvImportFileResult(
                    videos.First(video => video.ContainerPath == file.Path).Id,
                    Path.GetFileName(file.Path),
                    TvUploadStatus.Failed,
                    outcome.Message,
                    file.SeasonNumber ?? 0,
                    file.EpisodeIds?.FirstOrDefault() is int episodeId && byId.TryGetValue(episodeId, out EpisodeDto? episode)
                        ? episode.EpisodeNumber
                        : 0,
                    file.Quality?.Quality?.Name ?? "unknown")).ToList());
        }

        List<EpisodeDto> refreshedEpisodes = await sonarrClient.GetEpisodes(series.Id, ct);
        Dictionary<int, EpisodeDto> refreshedById = refreshedEpisodes
            .GroupBy(episode => episode.Id)
            .ToDictionary(group => group.Key, group => group.First());
        List<EpisodeFileDto> episodeFiles = await sonarrClient.GetEpisodeFiles(series.Id, ct);
        Dictionary<int, EpisodeFileDto> fileByEpisode = [];
        foreach (EpisodeFileDto episodeFile in episodeFiles)
        {
            foreach (int episodeId in episodeFile.EpisodeIds)
                fileByEpisode[episodeId] = episodeFile;
        }

        List<TvImportFileResult> results = [];
        List<TvUploadRecord> imported = [];
        List<int> importedEpisodeIds = [];

        foreach ((long uploadId, List<EpisodeDto> mapped) in mappedByUpload)
        {
            TvUploadRecord video = videos.First(record => record.Id == uploadId);
            bool hasFile = mapped.Any(episode => refreshedById.TryGetValue(episode.Id, out EpisodeDto? refreshed) && refreshed.HasFile);
            string quality = fileByEpisode.TryGetValue(mapped[0].Id, out EpisodeFileDto? file)
                ? file.Quality?.Quality?.Name ?? "unknown"
                : "unknown";

            video.Status = hasFile ? TvUploadStatus.Imported : TvUploadStatus.Failed;
            video.Error = hasFile ? null : "The episode file was not found after the import.";
            await store.UpdateAsync(video, ct);

            if (hasFile)
            {
                imported.Add(video);
                importedEpisodeIds.AddRange(mapped.Select(episode => episode.Id));
                results.Add(new TvImportFileResult(
                    video.Id,
                    video.FileName,
                    TvUploadStatus.Imported,
                    mapped.Count > 1
                        ? $"Imported {mapped[0].SeasonNumber:00}x{mapped[0].EpisodeNumber:00}-{mapped[^1].EpisodeNumber:00}"
                        : $"Imported {mapped[0].SeasonNumber:00}x{mapped[0].EpisodeNumber:00}",
                    mapped[0].SeasonNumber,
                    mapped[0].EpisodeNumber,
                    quality));
            }
            else
            {
                results.Add(new TvImportFileResult(
                    video.Id,
                    video.FileName,
                    TvUploadStatus.Failed,
                    video.Error ?? "Import did not produce a file.",
                    mapped[0].SeasonNumber,
                    mapped[0].EpisodeNumber,
                    quality));
            }
        }

        foreach (string failure in failures)
        {
            TvUploadRecord? failed = videos.FirstOrDefault(video => failure.StartsWith(video.FileName + ":", StringComparison.Ordinal));
            if (failed is not null)
            {
                results.Add(new TvImportFileResult(
                    failed.Id,
                    failed.FileName,
                    TvUploadStatus.Failed,
                    failure[(failed.FileName.Length + 1)..].Trim(),
                    failed.SeasonNumber,
                    failed.EpisodeNumber,
                    "unknown"));
            }
        }

        if (request.ImportSubtitles && subtitles.Count > 0)
        {
            foreach (TvUploadRecord subtitle in subtitles)
            {
                TvUploadRecord? owner = FindVideoForSubtitle(subtitle, videos);
                if (owner is null
                    || !mappedByUpload.TryGetValue(owner.Id, out List<EpisodeDto>? ownerEpisodes)
                    || !fileByEpisode.TryGetValue(ownerEpisodes[0].Id, out EpisodeFileDto? ownerFile))
                {
                    results.Add(new TvImportFileResult(
                        subtitle.Id,
                        subtitle.FileName,
                        TvUploadStatus.Failed,
                        "Could not match this subtitle to an imported episode.",
                        0,
                        0,
                        ""));
                    continue;
                }

                string localEpisodePath = pathMapper.MapToLocalPath(ownerFile.Path) ?? ownerFile.Path;
                TvImportFileResult result = CopySubtitle(subtitle, localEpisodePath);
                results.Add(result with { SeasonNumber = ownerEpisodes[0].SeasonNumber, EpisodeNumber = ownerEpisodes[0].EpisodeNumber });
                if (result.Status == TvUploadStatus.Imported)
                    imported.Add(subtitle);
            }
        }

        await FinishAsync(records, results, ct);

        if (imported.Count > 0)
            await bus.Publish(new MediaImportedEvent(
                "local-upload",
                series.Title,
                "Episode",
                SeriesId: series.Id,
                EpisodeIds: importedEpisodeIds.Count > 0 ? [.. importedEpisodeIds.Distinct()] : null), ct);

        int ok = results.Count(result => result.Status == TvUploadStatus.Imported);
        return new TvImportResult(
            request.BatchId,
            ok > 0,
            ok > 0
                ? $"Imported {ok} file{(ok == 1 ? "" : "s")} into {series.Title}."
                : "No files were imported.",
            0,
            series.Id,
            results);
    }

    async Task<SeriesDto> EnsureSeriesAsync(TvImportRequest request, CancellationToken ct)
    {
        SeriesDto? series = null;

        if (request.LibraryId > 0)
            series = await sonarrClient.GetSeries(request.LibraryId, ct);

        if (series is null && request.TvdbId > 0)
        {
            List<SeriesDto> all = await sonarrClient.GetSeries(ct);
            series = all.FirstOrDefault(candidate => candidate.TvdbId == request.TvdbId);
        }

        if (series is null)
        {
            if (request.TvdbId <= 0)
                throw new TvUploadException("Could not resolve the show to add. Search for it manually first.");

            int profileId = request.QualityProfileId > 0
                ? request.QualityProfileId
                : await sonarr.GetDefaultQualityProfileId(ct);

            HashSet<int>? seasons = request.MonitoredSeasons.Count > 0
                ? [.. request.MonitoredSeasons]
                : request.SeasonNumber > 0 ? [request.SeasonNumber] : null;
            int seriesId = await sonarr.AddSeriesWithOptions(
                request.TvdbId,
                request.Title,
                profileId,
                seasons,
                monitorFutureSeasons: true,
                searchForSeries: false,
                ct);

            series = await sonarrClient.GetSeries(seriesId, ct)
                ?? throw new TvUploadException("The show was added but could not be reopened.");
        }
        else if (request.SeasonNumber > 0)
        {
            await sonarr.ToggleSeasonMonitored(series.Id, request.SeasonNumber, true, ct);
        }

        if (request.EpisodeNumber > 0)
        {
            List<EpisodeDto> episodes = await sonarrClient.GetEpisodes(series.Id, ct);
            EpisodeDto? episode = episodes.FirstOrDefault(
                item => item.SeasonNumber == request.SeasonNumber && item.EpisodeNumber == request.EpisodeNumber);
            if (episode is not null && !episode.Monitored)
                await sonarrClient.UpdateEpisodeMonitored(episode.Id, true, ct);
        }

        return series;
    }

    static List<EpisodeDto> ResolveEpisodes(
        TvUploadRecord video,
        TvImportRequest request,
        int seriesId,
        Dictionary<(int Season, int Episode), EpisodeDto> byNumber,
        Dictionary<int, EpisodeDto> byId,
        Dictionary<string, ManualImportResourceDto> suggestions,
        HashSet<int> claimedEpisodes,
        out string? failure)
    {
        failure = null;
        TvParsedFile parsed = TvImportFilename.Parse(video.FileName);

        TvImportFileMapping? explicitMapping = request.Mappings.FirstOrDefault(mapping => mapping.UploadId == video.Id);
        if (explicitMapping is not null)
        {
            List<EpisodeDto> explicitEpisodes = EpisodesForMapping(explicitMapping, byNumber, byId);
            if (explicitEpisodes.Count > 0)
            {
                List<EpisodeDto> free = [.. explicitEpisodes.Where(episode => !claimedEpisodes.Contains(episode.Id))];
                if (free.Count == 0)
                    failure = "That episode is already covered by another file in this upload.";
                return free;
            }
        }

        if (suggestions.TryGetValue(video.ContainerPath, out ManualImportResourceDto? suggestion))
        {
            HashSet<int> suggestedIds = [.. suggestion.Episodes
                .Where(episode => episode.SeriesId == seriesId)
                .Select(episode => episode.Id)];

            List<EpisodeDto> suggested = [.. suggestedIds
                .Select(id => byId.GetValueOrDefault(id))
                .Where(episode => episode is not null)
                .Select(episode => episode!)
                .Where(episode => !claimedEpisodes.Contains(episode.Id))
                .OrderBy(episode => episode.SeasonNumber)
                .ThenBy(episode => episode.EpisodeNumber)];

            if (suggested.Count > 0)
                return suggested;
        }

        int season = parsed.SeasonNumber ?? request.SeasonNumber;
        int? start = parsed.EpisodeNumber ?? (request.EpisodeNumber > 0 ? request.EpisodeNumber : null);

        if (season > 0 && start is int first)
        {
            int end = parsed.EndEpisodeNumber ?? first;
            List<EpisodeDto> range = [];
            for (int number = first; number <= end; number++)
            {
                if (byNumber.TryGetValue((season, number), out EpisodeDto? episode)
                    && !claimedEpisodes.Contains(episode.Id))
                {
                    range.Add(episode);
                }
            }

            if (range.Count > 0)
                return range;
        }

        if (season > 0)
        {
            EpisodeDto? next = byNumber
                .Where(pair => pair.Key.Season == season && !claimedEpisodes.Contains(pair.Value.Id))
                .OrderBy(pair => pair.Key.Episode)
                .Select(pair => pair.Value)
                .FirstOrDefault();

            if (next is not null)
                return [next];
        }

        failure = string.IsNullOrEmpty(parsed.EpisodeLabel)
            ? "Could not work out the season and episode from the file name. Choose the episode in the upload dialog."
            : $"That episode ({parsed.EpisodeLabel}) is not in this show yet.";
        return [];
    }

    static List<EpisodeDto> EpisodesForMapping(
        TvImportFileMapping mapping,
        Dictionary<(int Season, int Episode), EpisodeDto> byNumber,
        Dictionary<int, EpisodeDto> byId)
    {
        if (mapping.EpisodeId > 0 && byId.TryGetValue(mapping.EpisodeId, out EpisodeDto? exact))
            return [exact];

        if (mapping.SeasonNumber <= 0 || mapping.EpisodeNumber <= 0)
            return [];

        int end = mapping.EndEpisodeNumber > mapping.EpisodeNumber
            ? mapping.EndEpisodeNumber
            : mapping.EpisodeNumber;

        List<EpisodeDto> episodes = [];
        for (int number = mapping.EpisodeNumber; number <= end; number++)
        {
            if (byNumber.TryGetValue((mapping.SeasonNumber, number), out EpisodeDto? episode))
                episodes.Add(episode);
        }

        return episodes;
    }

    async Task<ManualImportResourceDto?> ReprocessEpisodeAsync(
        TvUploadRecord video,
        int seriesId,
        List<EpisodeDto> mapped,
        CancellationToken ct)
    {
        try
        {
            List<object> items =
            [
                new
                {
                    path = video.ContainerPath,
                    seriesId,
                    seasonNumber = mapped[0].SeasonNumber,
                    episodeIds = mapped.Select(episode => episode.Id).ToArray(),
                    releaseType = mapped.Count > 1 ? "multiEpisode" : "singleEpisode"
                }
            ];

            List<ManualImportResourceDto> result = await sonarrClient.ReprocessManualImport(items, ct);
            return result.FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Sonarr could not reprocess the uploaded episode file");
            return null;
        }
    }

    async Task<List<ManualImportResourceDto>> SafeGetSonarrSuggestionsAsync(string batchId, CancellationToken ct)
    {
        try
        {
            return await sonarrClient.GetManualImport(store.GetContainerFolder(batchId), filterExistingFiles: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Sonarr could not read the upload staging folder");
            return [];
        }
    }

    //  Command and result plumbing

    static async Task<CommandOutcome> WaitForCommandAsync(
        ArrClient client,
        ArrCommandDto command,
        CancellationToken ct)
    {
        ArrCommandDto? current = command;
        DateTime deadline = DateTime.UtcNow + CommandTimeout;

        while (current is not null && DateTime.UtcNow < deadline)
        {
            string status = current.Status.ToLowerInvariant();
            if (status is "completed" or "failed" or "aborted")
            {
                if (status == "completed"
                    && string.Equals(current.Result, "successful", StringComparison.OrdinalIgnoreCase))
                {
                    return new CommandOutcome(true, current.Message ?? "Import completed.");
                }

                return new CommandOutcome(
                    false,
                    current.Message
                    ?? current.Exception
                    ?? $"The arr reported {status} while importing the file.");
            }

            await Task.Delay(CommandPollInterval, ct);
            current = await client.GetCommand(current.Id, ct);
        }

        return new CommandOutcome(false, "Timed out waiting for the arr to finish importing the file.");
    }

    sealed record CommandOutcome(bool Success, string Message);

    async Task FinishAsync(
        List<TvUploadRecord> records,
        List<TvImportFileResult> results,
        CancellationToken ct)
    {
        HashSet<long> handled = [.. results.Select(result => result.UploadId)];

        foreach (TvUploadRecord record in records)
        {
            TvImportFileResult? result = results.FirstOrDefault(item => item.UploadId == record.Id);
            if (result is not null)
            {
                record.Status = result.Status;
                record.Error = result.Status == TvUploadStatus.Imported ? null : result.Message;
            }
            else
            {
                record.Status = TvUploadStatus.Failed;
                record.Error = handled.Contains(record.Id) ? "This file was not imported." : "No import result was returned for this file.";
            }

            await store.UpdateAsync(record, ct);
        }

        List<TvUploadRecord> imported = [.. records.Where(record =>
            results.Any(result => result.UploadId == record.Id && result.Status == TvUploadStatus.Imported))];

        if (imported.Count > 0)
            store.RemoveImportedFiles(imported);
    }

    async Task MarkFailedAsync(IEnumerable<TvUploadRecord> records, string message, CancellationToken ct)
    {
        foreach (TvUploadRecord record in records)
        {
            record.Status = TvUploadStatus.Failed;
            record.Error = message;
            await store.UpdateAsync(record, ct);
        }
    }

    //  Subtitles

    static TvUploadRecord? FindVideoForSubtitle(TvUploadRecord subtitle, List<TvUploadRecord> videos)
    {
        TvParsedFile parsed = TvImportFilename.Parse(subtitle.FileName);
        if (parsed.SeasonNumber is int season && parsed.EpisodeNumber is int episode)
        {
            TvUploadRecord? exact = videos.FirstOrDefault(video =>
            {
                TvParsedFile other = TvImportFilename.Parse(video.FileName);
                return other.SeasonNumber == season && other.EpisodeNumber == episode;
            });
            if (exact is not null)
                return exact;
        }

        if (videos.Count == 1)
            return videos[0];

        string subtitleStem = TvImportFilename.Normalize(Path.GetFileNameWithoutExtension(subtitle.FileName));
        return videos
            .OrderByDescending(video =>
                TvImportMatcher.Similarity(
                    subtitleStem,
                    TvImportFilename.Normalize(Path.GetFileNameWithoutExtension(video.FileName))))
            .FirstOrDefault();
    }

    static TvImportFileResult CopySubtitle(
        TvUploadRecord subtitle,
        string destinationVideoPath)
    {
        try
        {
            string? localVideoPath = destinationVideoPath;
            if (string.IsNullOrWhiteSpace(localVideoPath))
                return new TvImportFileResult(subtitle.Id, subtitle.FileName, TvUploadStatus.Failed, "No imported video to attach this subtitle to", 0, 0, "");

            string directory = Path.GetDirectoryName(localVideoPath) ?? "";
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return new TvImportFileResult(subtitle.Id, subtitle.FileName, TvUploadStatus.Failed, "The imported video folder is not reachable", 0, 0, "");

            string suffix = SubtitleSuffix(subtitle.FileName);
            string destination = Path.Combine(
                directory,
                Path.GetFileNameWithoutExtension(localVideoPath) + suffix);

            File.Copy(subtitle.StoredPath, destination, overwrite: true);
            return new TvImportFileResult(subtitle.Id, subtitle.FileName, TvUploadStatus.Imported, "Subtitle copied beside the video", 0, 0, "");
        }
        catch (Exception ex)
        {
            return new TvImportFileResult(subtitle.Id, subtitle.FileName, TvUploadStatus.Failed, ex.Message, 0, 0, "");
        }
    }

    static string SubtitleSuffix(string fileName)
    {
        Match match = SubtitleLanguage.Match(fileName);
        if (!match.Success)
            return Path.GetExtension(fileName);

        string language = match.Groups["lang"].Value.ToLowerInvariant();
        string flag = match.Groups["flag"].Success ? "." + match.Groups["flag"].Value.ToLowerInvariant() : "";
        string extension = match.Groups["ext"].Value.ToLowerInvariant();
        return $".{language}{flag}.{extension}";
    }
}
