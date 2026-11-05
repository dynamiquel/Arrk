using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stackarr.Core;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.TestSupport;

namespace Stackarr.Transmission.Tests;

/// <summary>
/// Integration tests that run against a REAL Transmission Docker container.
/// These are skipped automatically (each test returns early) when Docker is unavailable.
/// </summary>
[NotInParallel("docker-transmission")]
public class TransmissionContainerIntegrationTests
{
    static readonly SemaphoreSlim FixtureLock = new(1, 1);
    static TransmissionFixture? _fixture;

    static async Task<TransmissionFixture?> GetFixtureAsync()
    {
        await FixtureLock.WaitAsync();
        try
        {
            if (_fixture is not null)
                return _fixture;

            TransmissionFixture fixture = new();
            if (!await fixture.StartAsync())
            {
                await fixture.DisposeAsync();
                return null;
            }

            _fixture = fixture;
            return _fixture;
        }
        finally
        {
            FixtureLock.Release();
        }
    }

    // Disposed at the end of the test session (see TearDown below) so the container and its
    // run-unique network are always removed.
    static TransmissionService CreateService(TransmissionClient client, TorrentDownloaderConfig? config = null)
        => new(client, new MutableOptionsMonitor<TorrentDownloaderConfig>(config ?? new TorrentDownloaderConfig()),
            NullLogger<TransmissionService>.Instance);

    [After(TestSession)]
    public static async Task TearDown()
    {
        if (_fixture is not null)
        {
            await _fixture.DisposeAsync();
            _fixture = null;
        }
    }

    [Test]
    public async Task Session_GetAndStats_Work()
    {
        TransmissionFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        using TransmissionClient client = fixture.CreateClient();

        TransmissionSession session = await client.GetSession();
        TransmissionSessionStats stats = await client.GetSessionStats();

        await Assert.That(session.Version).IsNotEmpty();
        await Assert.That(stats.TorrentCount).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task AddTorrent_WithRealBytes_AppearsInList()
    {
        TransmissionFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        using TransmissionClient client = fixture.CreateClient();
        TransmissionService service = CreateService(client);

        try
        {
            byte[] torrent = MinimalTorrentFile("integration-add");
            long id = await client.AddTorrent(torrent, "/downloads", ["stackarr-test"]);
            await Assert.That(id).IsGreaterThan(0);

            await Task.Delay(1500);
            List<TransmissionTorrent> torrents = await client.GetTorrents();
            await Assert.That(torrents.Any(t => t.Id == id)).IsTrue();

            List<TorrentItem> mapped = await service.GetTorrents("all");
            await Assert.That(mapped.Any(t => t.Hash == torrents.First(x => x.Id == id).HashString)).IsTrue();
        }
        finally
        {
            await RemoveAllAsync(client);
        }
    }

    [Test]
    public async Task PauseAndResume_RealTorrent_ChangesState()
    {
        TransmissionFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        using TransmissionClient client = fixture.CreateClient();
        TransmissionService service = CreateService(client);

        try
        {
            byte[] torrent = MinimalTorrentFile("integration-pause");
            await client.AddTorrent(torrent, "/downloads");
            await Task.Delay(1500);

            TransmissionTorrent added = (await client.GetTorrents()).First();

            await service.PauseTorrent(added.HashString);
            await Task.Delay(1200);
            TransmissionTorrent? paused = await client.GetTorrentById(added.Id);
            await Assert.That(paused!.Status).IsEqualTo(TransmissionStatus.Stopped);

            await service.ResumeTorrent(added.HashString);
            await Task.Delay(1200);
            TransmissionTorrent? resumed = await client.GetTorrentById(added.Id);
            await Assert.That(resumed!.Status).IsNotEqualTo(TransmissionStatus.Stopped);
        }
        finally
        {
            await RemoveAllAsync(client);
        }
    }

    [Test]
    public async Task DeleteTorrent_ByHash_ActuallyRemoves()
    {
        TransmissionFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        using TransmissionClient client = fixture.CreateClient();
        TransmissionService service = CreateService(client);

        try
        {
            byte[] torrent = MinimalTorrentFile("integration-delete");
            await client.AddTorrent(torrent, "/downloads");
            await Task.Delay(1500);
            string hash = (await client.GetTorrents()).First().HashString;

            // Hash (not a numeric id) must be accepted end-to-end.
            await service.DeleteTorrent(hash, TorrentFileCleanup.KeepFiles);
            await Task.Delay(1500);

            await Assert.That((await client.GetTorrents()).Any(t => t.HashString == hash)).IsFalse();
        }
        finally
        {
            await RemoveAllAsync(client);
        }
    }

    [Test]
    public async Task Filters_ReturnCorrectSubsets()
    {
        TransmissionFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        using TransmissionClient client = fixture.CreateClient();
        TransmissionService service = CreateService(client);

        try
        {
            await client.AddTorrent(MinimalTorrentFile("filter-a"), "/downloads");
            await Task.Delay(1500);

            TransmissionTorrent added = (await client.GetTorrents()).First();
            await client.PauseTorrent(added.HashString);
            await Task.Delay(1200);

            List<TorrentItem> paused = await service.GetTorrents("paused");
            List<TorrentItem> seeding = await service.GetTorrents("seeding");

            await Assert.That(paused.Any(t => t.Hash == added.HashString)).IsTrue();
            await Assert.That(seeding.Any(t => t.Hash == added.HashString)).IsFalse();
        }
        finally
        {
            await RemoveAllAsync(client);
        }
    }

    [Test]
    public async Task ApplyConfiguredLimits_RealSession_UpdatesQueueSizes()
    {
        TransmissionFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        using TransmissionClient client = fixture.CreateClient();
        TorrentDownloaderConfig config = new()
        {
            Speed = new SpeedConfig { UploadMbps = 0, DownloadMbps = 0 },
            Limits = new TorrentLimitsConfig { MaxActiveDownloads = 4, MaxActiveSeeds = 7 }
        };
        TransmissionService service = CreateService(client, config);

        await service.ApplyConfiguredLimits();

        TransmissionSession session = await client.GetSession();
        await Assert.That(session.DownloadQueueSize).IsEqualTo(4);
        await Assert.That(session.DownloadQueueEnabled).IsTrue();
        await Assert.That(session.SeedQueueSize).IsEqualTo(7);
        await Assert.That(session.SeedQueueEnabled).IsTrue();
    }

    [Test]
    public async Task GetLabels_ReflectsAddedLabels()
    {
        TransmissionFixture? fixture = await GetFixtureAsync();
        if (fixture is null) return;

        using TransmissionClient client = fixture.CreateClient();

        try
        {
            await client.AddTorrent(MinimalTorrentFile("label-test"), "/downloads", ["mylabel"]);
            await Task.Delay(1500);

            List<string> labels = await client.GetLabels();
            await Assert.That(labels).Contains("mylabel");
        }
        finally
        {
            await RemoveAllAsync(client);
        }
    }

    static async Task RemoveAllAsync(TransmissionClient client)
    {
        try
        {
            List<TransmissionTorrent> torrents = await client.GetTorrents();
            string[] hashes = torrents.Select(t => t.HashString).ToArray();
            if (hashes.Length > 0)
                await client.RemoveTorrents(hashes, TorrentFileCleanup.KeepFiles);
        }
        catch { }
    }

    static byte[] MinimalTorrentFile(string name)
    {
        // A valid piece hash is required; an empty "pieces" value is rejected by Transmission.
        byte[] pieceData = System.Text.Encoding.ASCII.GetBytes(new string('A', 16384));
        byte[] pieceHash = System.Security.Cryptography.SHA1.HashData(pieceData);

        Dictionary<string, object> dict = new()
        {
            ["announce"] = "http://example.com/announce",
            ["info"] = new Dictionary<string, object>
            {
                ["name"] = name,
                ["piece length"] = 16384,
                ["pieces"] = pieceHash,
                ["length"] = pieceData.Length
            }
        };
        return BencodeEncode(dict);
    }

    static byte[] BencodeEncode(object obj)
    {
        using MemoryStream ms = new();
        using BinaryWriter writer = new(ms);
        BencodeWrite(writer, obj);
        return ms.ToArray();
    }

    static void BencodeWrite(BinaryWriter writer, object obj)
    {
        switch (obj)
        {
            case Dictionary<string, object> dict:
                writer.Write((byte)'d');
                foreach (KeyValuePair<string, object> kvp in dict.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    BencodeWrite(writer, kvp.Key);
                    BencodeWrite(writer, kvp.Value);
                }
                writer.Write((byte)'e');
                break;
            case int num:
                writer.Write(System.Text.Encoding.ASCII.GetBytes($"i{num}e"));
                break;
            case string str:
                byte[] strBytes = System.Text.Encoding.ASCII.GetBytes(str);
                writer.Write(System.Text.Encoding.ASCII.GetBytes($"{strBytes.Length}:"));
                writer.Write(strBytes);
                break;
            case byte[] bytes:
                writer.Write(System.Text.Encoding.ASCII.GetBytes($"{bytes.Length}:"));
                writer.Write(bytes);
                break;
        }
    }
}
