using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.Ai;
using Arrk.Ai.Data;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Events;
using Arrk.TestSupport;
using Arrk.Tvarr;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Configurations;
using Arrk.Tvarr.Models;
using Arrk.Tvarr.Services;

namespace Arrk.IntegrationTests;

/// <summary>
/// Destructive live verification of the local-import pipeline against the running dev arrs. It
/// removes one existing movie/episode file, stages a copy of it, and lets the new pipeline import
/// it again. Gated behind <c>ARRK_LIVE_IMPORT=1</c> plus explicit Radarr/Sonarr URLs so a normal
/// hermetic run never touches a library.
/// </summary>
[NotInParallel("docker")]
public class LocalImportLiveTests
{
    static string? RadarrUrl => Environment.GetEnvironmentVariable("ARRK_LIVE_RADARR_URL");
    static string? SonarrUrl => Environment.GetEnvironmentVariable("ARRK_LIVE_SONARR_URL");
    static bool Enabled => Environment.GetEnvironmentVariable("ARRK_LIVE_IMPORT") == "1"
        && !string.IsNullOrWhiteSpace(RadarrUrl)
        && !string.IsNullOrWhiteSpace(SonarrUrl);

    const string DownloadsRoot = "/mnt/arrk/downloads";
    const string MoviesRoot = "/mnt/arrk/media/tv/movies";
    const string ShowsRoot = "/mnt/arrk/media/tv/shows";

    sealed class LiveDbFactory : IDbContextFactory<ArrkDbContext>
    {
        static readonly IModuleEntityConfiguration[] Configurations =
        [
            new TvUploadRecordConfiguration()
        ];

        readonly DbContextOptions<ArrkDbContext> _options;
        bool _created;

        public LiveDbFactory()
        {
            string path = Path.Combine(Path.GetTempPath(), $"stackarr-live-import-{Guid.NewGuid():N}.db");
            _options = new DbContextOptionsBuilder<ArrkDbContext>()
                .UseSqlite($"Data Source={path}")
                .Options;
        }

        public ArrkDbContext CreateDbContext()
        {
            ArrkDbContext db = new(_options, Configurations);
            if (!_created)
            {
                db.Database.EnsureCreated();
                _created = true;
            }
            return db;
        }

        public ValueTask<ArrkDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }

    sealed record Fixture(
        TvUploadStore Store,
        TvImportService Service,
        RadarrClient RadarrClient,
        SonarrClient SonarrClient,
        ArrMediaPathMapper Mapper);

    static Fixture Build()
    {
        LiveDbFactory db = new();
        ArrkConfig config = new()
        {
            Storage = new StorageConfig { Downloads = DownloadsRoot, Media = "/mnt/arrk/media" }
        };
        TvConfig tv = new() { Uploads = new TvUploadConfig { MaxFileSizeGb = 200 } };

        TvUploadStore store = new(
            db,
            new MutableOptionsMonitor<ArrkConfig>(config),
            new MutableOptionsMonitor<TvConfig>(tv),
            NullLogger<TvUploadStore>.Instance);

        RadarrClient radarrClient = new(RadarrUrl!, "arrk");
        SonarrClient sonarrClient = new(SonarrUrl!, "arrk");
        RadarrService radarr = new(radarrClient, NullLogger<RadarrService>.Instance);
        SonarrService sonarr = new(sonarrClient, NullLogger<SonarrService>.Instance);
        TmdbService tmdb = new("", "", NullLogger<TmdbService>.Instance);
        MediaCatalogService catalog = new(tmdb, radarr, sonarr, NullLogger<MediaCatalogService>.Instance);

        AiConfig aiConfig = new() { Enabled = false };
        MutableOptionsMonitor<AiConfig> aiMonitor = new(aiConfig);
        TvImportAiMatcher aiMatcher = new(
            new AiService(
                new AiClientFactory(NullLogger<AiClientFactory>.Instance),
                aiMonitor,
                NullLogger<AiService>.Instance),
            new AiDecisionService(db, TimeProvider.System),
            aiMonitor,
            NullLogger<TvImportAiMatcher>.Instance);

        ArrMediaPathMapper mapper = new(
        [
            ("/movies", MoviesRoot),
            ("/shows", ShowsRoot)
        ]);

        TvImportService service = new(
            store,
            radarrClient,
            sonarrClient,
            radarr,
            sonarr,
            catalog,
            aiMatcher,
            mapper,
            new EventBus(),
            NullLogger<TvImportService>.Instance);

        return new(store, service, radarrClient, sonarrClient, mapper);
    }

    [Test]
    public async Task Movie_DeleteAndReupload_ThroughLocalImport()
    {
        if (!Enabled) return;
        Fixture fixture = Build();
        string batch = Guid.NewGuid().ToString("N");

        MovieDto? movie = null;
        MovieFileDto? file = null;
        foreach (MovieDto candidate in await fixture.RadarrClient.GetMovies())
        {
            if (!candidate.HasFile) continue;
            List<MovieFileDto> files = await fixture.RadarrClient.GetMovieFiles(candidate.Id);
            MovieFileDto? smallest = files.OrderBy(item => item.Size).FirstOrDefault();
            if (smallest is null) continue;
            if (file is null || smallest.Size < file.Size)
            {
                movie = candidate;
                file = smallest;
            }
        }

        if (movie is null || file is null) return;
        string localPath = fixture.Mapper.MapToLocalPath(file.Path) ?? file.Path;
        if (!File.Exists(localPath)) return;

        TvUploadRecord staged;
        using (FileStream source = File.OpenRead(localPath))
        {
            staged = await fixture.Store.StageAsync(
                source, Path.GetFileName(localPath), new FileInfo(localPath).Length, batch, "movie", "movie");
        }

        try
        {
            TvImportAnalysis analysis = await fixture.Service.AnalyzeAsync(
                batch,
                new TvImportContext(false, "movie", movie.TmdbId, 0, movie.Id, 0, 0, movie.Title, movie.Year, ""));

            await Assert.That(analysis.Match).IsNotNull();
            await Assert.That(analysis.Match!.Title).IsEqualTo(movie.Title);

            foreach (MovieFileDto existing in await fixture.RadarrClient.GetMovieFiles(movie.Id))
                await fixture.RadarrClient.DeleteMovieFile(existing.Id);

            TvImportResult result = await fixture.Service.ImportAsync(new TvImportRequest
            {
                BatchId = batch,
                MediaType = "movie",
                LibraryId = movie.Id,
                TmdbId = movie.TmdbId,
                Title = movie.Title,
                Year = movie.Year
            });

            if (!result.Success)
            {
                if (File.Exists(staged.StoredPath))
                    File.Copy(staged.StoredPath, localPath, overwrite: true);
                await fixture.RadarrClient.CommandRescanMovie(movie.Id);
            }

            await Assert.That(result.Success).IsTrue();
            MovieDto refreshed = (await fixture.RadarrClient.GetMovie(movie.Id))!;
            await Assert.That(refreshed.HasFile).IsTrue();
        }
        finally
        {
            await fixture.Store.CancelBatchAsync(batch);
        }
    }

    [Test]
    public async Task Episode_DeleteAndReupload_ThroughLocalImport()
    {
        if (!Enabled) return;
        Fixture fixture = Build();
        string batch = Guid.NewGuid().ToString("N");

        SeriesDto? series = null;
        EpisodeDto? episode = null;
        EpisodeFileDto? file = null;

        foreach (SeriesDto candidate in await fixture.SonarrClient.GetSeries())
        {
            if ((candidate.Statistics?.EpisodeFileCount ?? 0) == 0) continue;
            List<EpisodeDto> episodes = await fixture.SonarrClient.GetEpisodes(candidate.Id);
            EpisodeDto? withFile = episodes.FirstOrDefault(item => item.HasFile && item.EpisodeFileId > 0);
            if (withFile is null) continue;

            List<EpisodeFileDto> files = await fixture.SonarrClient.GetEpisodeFiles(candidate.Id);
            EpisodeFileDto? match = files.FirstOrDefault(item => item.Id == withFile.EpisodeFileId);
            if (match is null) continue;

            series = candidate;
            episode = withFile;
            file = match;
            break;
        }

        if (series is null || episode is null || file is null) return;
        string localPath = fixture.Mapper.MapToLocalPath(file.Path) ?? file.Path;
        if (!File.Exists(localPath)) return;

        TvUploadRecord staged;
        using (FileStream source = File.OpenRead(localPath))
        {
            staged = await fixture.Store.StageAsync(
                source, Path.GetFileName(localPath), new FileInfo(localPath).Length, batch, "episode", "tv");
        }

        try
        {
            TvImportAnalysis analysis = await fixture.Service.AnalyzeAsync(
                batch,
                new TvImportContext(
                    false,
                    "tv",
                    series.TmdbId,
                    series.TvdbId,
                    series.Id,
                    episode.SeasonNumber,
                    episode.EpisodeNumber,
                    series.Title,
                    series.Year,
                    ""));
            await Assert.That(analysis.Match).IsNotNull();

            await fixture.SonarrClient.DeleteEpisodeFile(file.Id);

            TvImportResult result = await fixture.Service.ImportAsync(new TvImportRequest
            {
                BatchId = batch,
                MediaType = "tv",
                LibraryId = series.Id,
                TvdbId = series.TvdbId,
                Title = series.Title,
                Mappings =
                [
                    new TvImportFileMapping(staged.Id, episode.SeasonNumber, episode.EpisodeNumber, EpisodeId: episode.Id)
                ]
            });

            if (!result.Success)
            {
                if (File.Exists(staged.StoredPath))
                    File.Copy(staged.StoredPath, localPath, overwrite: true);
                await fixture.SonarrClient.CommandRescanSeries(series.Id);
            }

            await Assert.That(result.Success).IsTrue();
            EpisodeDto refreshed = (await fixture.SonarrClient.GetEpisode(episode.Id))!;
            await Assert.That(refreshed.HasFile).IsTrue();
        }
        finally
        {
            await fixture.Store.CancelBatchAsync(batch);
        }
    }
}
