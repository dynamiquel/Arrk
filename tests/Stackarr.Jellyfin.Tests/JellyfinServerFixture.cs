using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Auth.Abstractions;
using Stackarr.TestSupport;

namespace Stackarr.Jellyfin.Tests;

/// <summary>
/// A real Jellyfin container used by the integration tests. It is started with a temporary
/// media tree bind-mounted at the same container paths Stackarr uses (/movies, /shows, /music)
/// and tiny generated videos so library scans and deep-link resolution exercise the real
/// server. The startup wizard is completed exactly once during <see cref="StartAsync"/>, and the
/// before/after state is captured so tests can assert on it deterministically.
/// </summary>
public sealed class JellyfinServer : IAsyncDisposable
{
    public static readonly string ContainerName = $"stackarr-test-jellyfin-{TestRun.Id}";
    public const string AdminUsername = "admin";
    public const string AdminPassword = "jellyfin-it-pass";
    public const int TmdbMovieId = 1273221;
    public const int TvdbSeriesId = 121361;

    public string ContainerId { get; private set; } = "";
    public int Port { get; private set; }
    public string RootDir { get; private set; } = "";
    public bool Ready { get; private set; }
    public bool HasMedia { get; private set; }

    public JellyfinConfig Config { get; private set; } = new();
    public JellyfinPublicSystemInfo PublicInfoBeforeConfigure { get; private set; } = new();
    public JellyfinConfigureResult FirstConfigureResult { get; private set; }
    public JellyfinConfigureResult ReconciledResult { get; private set; }

    public string Url => $"http://localhost:{Port}";

    public JellyfinClient CreateClient(string username = AdminUsername, string password = AdminPassword) =>
        new(Url, username, password, "stackarr-test");

    /// <summary>
    /// Returns false only when Docker is unavailable (the tests then soft-skip). Any failure to
    /// actually start/configure Jellyfin propagates, so a broken fixture fails the tests instead
    /// of silently turning every test into a no-op.
    /// </summary>
    public async Task<bool> StartAsync()
    {
        if (!await DockerTestContainer.IsDockerAvailable())
            return false;

        await StartCoreAsync();
        return true;
    }

    async Task StartCoreAsync()
    {
        RootDir = Path.Combine(Path.GetTempPath(), $"stackarr-jellyfin-it-{Guid.NewGuid():N}");
        string movieDir = Path.Combine(RootDir, "movies", TmdbMovieId.ToString());
        string showDir = Path.Combine(RootDir, "shows", TvdbSeriesId.ToString(), "Season 01");
        Directory.CreateDirectory(movieDir);
        Directory.CreateDirectory(showDir);
        Directory.CreateDirectory(Path.Combine(RootDir, "music"));

        HasMedia = GenerateSample(Path.Combine(movieDir, "Scary Movie (2026).mkv"))
            && GenerateSample(Path.Combine(showDir, "Some Show S01E01.mkv"))
            && GenerateSample(Path.Combine(showDir, "Some Show S01E02.mkv"))
            && GenerateSample(Path.Combine(showDir, "Some Show S01E03.mkv"));

        await DockerTestContainer.RemoveContainerByName(ContainerName);
        await DockerTestContainer.EnsureImage("lscr.io/linuxserver/jellyfin:latest");

        (ContainerId, Port) = await DockerTestContainer.Start(
            "lscr.io/linuxserver/jellyfin:latest",
            ContainerName,
            null,
            8096,
            new Dictionary<string, string>
            {
                ["PUID"] = "1000",
                ["PGID"] = "1000",
                ["TZ"] = "Etc/UTC"
            },
            binds:
            [
                $"{Path.Combine(RootDir, "movies")}:/movies",
                $"{Path.Combine(RootDir, "shows")}:/shows",
                $"{Path.Combine(RootDir, "music")}:/music"
            ]);

        if (!await WaitForStartupReadyAsync(TimeSpan.FromMinutes(5)))
            throw new TimeoutException("Jellyfin did not become ready for the startup wizard in time");

        Config = new JellyfinConfig
        {
            ServerName = "Stackarr",
            AdminUsername = AdminUsername,
            AdminPassword = AdminPassword,
            MetadataLanguage = "en",
            MetadataCountry = "US"
        };

        JellyfinClient client = CreateClient();
        PublicInfoBeforeConfigure = await client.GetPublicSystemInfoAsync();

        JellyfinService service = new(client, new MutableOptionsMonitor<JellyfinConfig>(Config), new MutableOptionsMonitor<AuthConfig>(new AuthConfig()), NullLogger<JellyfinService>.Instance);
        FirstConfigureResult = await ConfigureWithRetryAsync(service, TimeSpan.FromMinutes(2));

        // Wait for the initial scan, then run the (idempotent) configuration again so the
        // folder-id metadata reconciliation is guaranteed to have run against real items.
        if (HasMedia)
        {
            await WaitForItemsAsync("Movie", TimeSpan.FromMinutes(3));
            await WaitForItemsAsync("Series", TimeSpan.FromMinutes(3));
        }
        ReconciledResult = await service.ConfigureAsync();

        Ready = true;
    }

    async Task<JellyfinConfigureResult> ConfigureWithRetryAsync(JellyfinService service, TimeSpan timeout)
    {
        Stopwatch sw = Stopwatch.StartNew();
        Exception? last = null;

        while (sw.Elapsed < timeout)
        {
            try
            {
                return await service.ConfigureAsync();
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(3000);
            }
        }

        throw last ?? new TimeoutException("Jellyfin configuration did not succeed in time");
    }

    /// <summary>Jellyfin answers /System/Info/Public before it is ready; the startup endpoint 503s until fully up.</summary>
    async Task<bool> WaitForStartupReadyAsync(TimeSpan timeout)
    {
        JellyfinClient client = CreateClient();
        Stopwatch sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            try
            {
                await client.GetStartupConfigurationAsync();
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                /* still starting */
            }
            catch
            {
                /* connection refused while the port is coming up */
            }

            await Task.Delay(2000);
        }

        return false;
    }

    public JellyfinService CreateService(JellyfinConfig config) =>
        new(CreateClient(), new MutableOptionsMonitor<JellyfinConfig>(config), new MutableOptionsMonitor<AuthConfig>(new AuthConfig()), NullLogger<JellyfinService>.Instance);

    public JellyfinLinkService CreateLinkService(JellyfinConfig? jellyfin = null) =>
        new(
            CreateClient(),
            new MutableOptionsMonitor<JellyfinConfig>(jellyfin ?? Config),
            new MutableOptionsMonitor<Stackarr.Core.Configuration.RoutesConfig>(new Stackarr.Core.Configuration.RoutesConfig()),
            NullLogger<JellyfinLinkService>.Instance);

    /// <summary>Repeatedly refreshes the library until items of the requested type appear.</summary>
    public async Task<JellyfinItemsResult> WaitForItemsAsync(string includeItemTypes, TimeSpan timeout, CancellationToken ct = default)
    {
        JellyfinClient client = CreateClient();
        Stopwatch sw = Stopwatch.StartNew();
        JellyfinItemsResult result = new();

        while (sw.Elapsed < timeout)
        {
            await client.TriggerLibraryRefreshAsync(ct);
            result = await client.GetItemsAsync(new JellyfinItemQuery
            {
                Recursive = true,
                IncludeItemTypes = includeItemTypes,
                Fields = ["ProviderIds", "Path"],
                Limit = 100
            }, ct);

            if (result.Items.Count > 0)
                return result;

            await Task.Delay(3000, ct);
        }

        return result;
    }

    /// <summary>
    /// Generates a tiny MKV with two audio tracks and two text subtitle tracks, which is what the
    /// player's track-selection tests need. A plain one-stream sample cannot prove that an explicit
    /// <c>AudioStreamIndex</c>/<c>SubtitleStreamIndex</c> is honoured. The clip is six minutes long
    /// so Jellyfin's resume threshold (<c>MinResumeDurationSeconds</c>, five minutes) accepts it.
    /// </summary>
    static bool GenerateSample(string path)
    {
        try
        {
            const int seconds = 360;
            string directory = Path.GetDirectoryName(path)!;
            string englishSub = Path.Combine(directory, "sample-eng.srt");
            string spanishSub = Path.Combine(directory, "sample-spa.srt");
            File.WriteAllText(englishSub, "1\n00:00:00,000 --> 00:00:02,000\nEnglish\n");
            File.WriteAllText(spanishSub, "1\n00:00:00,000 --> 00:00:02,000\nEspañol\n");

            string arguments =
                "-y -loglevel error " +
                $"-f lavfi -i color=c=black:s=64x64:d={seconds} " +
                $"-f lavfi -i sine=frequency=440:duration={seconds} " +
                $"-f lavfi -i sine=frequency=880:duration={seconds} " +
                $"-i \"{englishSub}\" -i \"{spanishSub}\" " +
                "-map 0:v -map 1:a -map 2:a -map 3:s -map 4:s " +
                "-c:v libx264 -pix_fmt yuv420p -c:a aac -c:s ass " +
                "-metadata:s:a:0 language=eng -metadata:s:a:1 language=jpn " +
                "-metadata:s:s:0 language=eng -metadata:s:s:1 language=spa " +
                "-disposition:s:0 default " +
                $"\"{path}\"";

            ProcessStartInfo psi = new("ffmpeg", arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using Process? process = Process.Start(psi);
            process?.WaitForExit(60_000);
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DockerTestContainer.RemoveContainer(ContainerId);
        try
        {
            if (Directory.Exists(RootDir))
                Directory.Delete(RootDir, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }
}

/// <summary>
/// Shares a single configured <see cref="JellyfinServer"/> across the Jellyfin integration test
/// classes. All consumers must be marked <c>[NotInParallel("docker")]</c>.
/// </summary>
public static class JellyfinServerShared
{
    public const string ParallelKey = "docker";

    static readonly SemaphoreSlim Lock = new(1, 1);
    static JellyfinServer? _server;
    static Exception? _failure;
    static bool _attempted;

    public static async Task<JellyfinServer?> GetServerAsync()
    {
        await Lock.WaitAsync();
        try
        {
            // A failure to start/configure the shared server must fail every test, not silently
            // skip them.
            if (_failure is not null)
                throw _failure;

            if (_attempted)
                return _server;

            _attempted = true;
            JellyfinServer server = new();
            try
            {
                if (!await server.StartAsync())
                {
                    await server.DisposeAsync();
                    _server = null;
                    return null;
                }

                _server = server;
                return _server;
            }
            catch (Exception ex)
            {
                _failure = ex;
                await server.DisposeAsync();
                throw;
            }
        }
        finally
        {
            Lock.Release();
        }
    }

    [After(TestSession)]
    public static async Task TearDown()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
            _server = null;
        }
    }
}
