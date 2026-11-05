using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Stackarr.Ai;
using Stackarr.Core.Events;
using Stackarr.Core.Models;
using Stackarr.Core.Services;
using Stackarr.DownloadManagement.Tools;

namespace Stackarr.DownloadManagement.Tests;

/// <summary>
/// The downloads pack. Progress maps the torrent's 0–1 progress to a percentage and filters by release
/// text; pausing is an administrator action. The fake downloader keeps the real interface so the
/// tool is exercised through the same contract the module uses.
/// </summary>
public class DownloadsAssistantToolTests
{
    static AssistantToolContext Context(ITorrentDownloader? torrents = null, bool admin = false)
    {
        ServiceCollection services = new();
        if (torrents is not null)
            services.AddSingleton(torrents);
        return new AssistantToolContext(
            1, "alice", admin, services.BuildServiceProvider(), CancellationToken.None);
    }

    static AIFunction Tool(AssistantToolContext context, string name) =>
        (AIFunction)new DownloadsAssistantTool().CreateTools(context).Single(t => t.Name == name);

    [Test]
    public async Task Progress_MapsPercentAndFiltersByTitle()
    {
        FakeTorrents torrents = new(
        [
            new TorrentItem
            {
                Hash = "abc", Name = "Severance.S02E01.1080p.WEB-DL", Category = "tv",
                Progress = 0.42, State = TorrentState.Downloading, DownloadSpeed = 1024,
                Size = 1000, Downloaded = 420, RemainingTime = "00:10:00", Seeds = 5, Peers = 3
            },
            new TorrentItem
            {
                Hash = "def", Name = "Dune.Part.Two.2160p", Category = "movies",
                Progress = 1.0, State = TorrentState.Seeding
            }
        ]);

        JsonElement json = (JsonElement)(await Tool(Context(torrents), "downloads_progress").InvokeAsync(
            new AIFunctionArguments { ["query"] = "severance", ["status"] = "downloading" }))!;

        JsonElement items = json.GetProperty("items");
        await Assert.That(items.GetArrayLength()).IsEqualTo(1);
        await Assert.That(items[0].GetProperty("name").GetString()).Contains("Severance");
        await Assert.That(items[0].GetProperty("progressPercent").GetInt32()).IsEqualTo(42);
        await Assert.That(items[0].GetProperty("id").GetString()).IsEqualTo("abc");
    }

    [Test]
    public async Task Pause_IsAdminOnly()
    {
        FakeTorrents torrents = new([]);

        JsonElement json = (JsonElement)(await Tool(Context(torrents, admin: false), "downloads_pause").InvokeAsync(
            new AIFunctionArguments { ["id"] = "abc" }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(torrents.Paused).IsEmpty();
    }

    [Test]
    public async Task Pause_AdminPausesTheHash()
    {
        FakeTorrents torrents = new(
        [
            new TorrentItem { Hash = "abc", Name = "Severance", State = TorrentState.Downloading }
        ]);

        JsonElement json = (JsonElement)(await Tool(Context(torrents, admin: true), "downloads_pause").InvokeAsync(
            new AIFunctionArguments { ["id"] = "abc" }))!;

        await Assert.That(json.GetProperty("ok").GetBoolean()).IsTrue();
        await Assert.That(torrents.Paused).IsEquivalentTo(new[] { "abc" });
    }

    [Test]
    public async Task Failures_ReadTheDownloadEventLog()
    {
        FakeTorrents torrents = new([]);
        ServiceCollection services = new();
        services.AddSingleton<ITorrentDownloader>(torrents);
        services.AddSingleton<IEventStore>(new FakeEvents());
        AssistantToolContext context = new(
            1, "alice", false, services.BuildServiceProvider(), CancellationToken.None);

        JsonElement json = (JsonElement)(await Tool(context, "downloads_failures").InvokeAsync(
            new AIFunctionArguments { ["limit"] = 5 }))!;

        await Assert.That(json.GetProperty("count").GetInt32()).IsEqualTo(1);
        await Assert.That(json.GetProperty("items")[0].GetProperty("message").GetString())
            .Contains("stalled");
    }

    sealed class FakeTorrents(List<TorrentItem> items) : ITorrentDownloader
    {
        public List<string> Paused { get; } = [];

        public Task<TorrentItem?> GetTorrent(string id, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(t => t.Hash == id));

        public Task<string> AddTorrent(byte[] torrentData, string category = "", string savePath = "", CancellationToken ct = default) =>
            Task.FromResult("");

        public Task<string> AddTorrent(Uri torrentUri, string category = "", string savePath = "", CancellationToken ct = default) =>
            Task.FromResult("");

        public Task DeleteTorrent(string id, TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task PauseTorrent(string id, CancellationToken ct = default)
        {
            Paused.Add(id);
            return Task.CompletedTask;
        }

        public Task ResumeTorrent(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<TorrentTransferStats> GetTransferStats(CancellationToken ct = default) =>
            Task.FromResult(new TorrentTransferStats());

        public Task<List<TorrentItem>> GetTorrents(string filter = "all", CancellationToken ct = default) =>
            Task.FromResult(items);

        public Task PauseTorrents(string filter = "all", CancellationToken ct = default) => Task.CompletedTask;

        public Task ResumeTorrents(string filter = "all", CancellationToken ct = default) => Task.CompletedTask;
    }

    sealed class FakeEvents : IEventStore
    {
        public Task RecordAsync(SystemEventRecord record, CancellationToken ct = default) => Task.CompletedTask;

        public Task RecordAsync(IEnumerable<SystemEventRecord> records, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<SystemEvent>> QueryAsync(SystemEventQuery query, CancellationToken ct = default)
        {
            IReadOnlyList<SystemEvent> rows = query.EventType == DownloadEventType.Stalled
                ?
                [
                    new SystemEvent
                    {
                        Timestamp = DateTime.UtcNow,
                        Category = EventCategory.Download,
                        EventType = DownloadEventType.Stalled,
                        Severity = "Warning",
                        Message = "Severance is stalled",
                        Source = "abc",
                        SourceName = "Severance.S02E01"
                    }
                ]
                : [];
            return Task.FromResult(rows);
        }

        public Task<int> PurgeOlderThanAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> GetEventTypesAsync(string? category = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }
}
