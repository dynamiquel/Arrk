using Arrk.Core.Services;

using Arrk.TestSupport;
namespace Arrk.Transmission.Tests;

/// <summary>
/// In-memory <see cref="TransmissionClient"/> that records every call and returns canned data,
/// used to test <see cref="TransmissionService"/> in isolation.
/// </summary>
public sealed class FakeTransmissionClient : TransmissionClient
{
    public FakeTransmissionClient()
        : base("http://localhost:9091/", "transmission", "arrk", new FakeHttpMessageHandler())
    {
    }

    public List<TransmissionTorrent> Torrents { get; set; } = [];
    public TransmissionSession Session { get; set; } = new();
    public TransmissionSessionStats Stats { get; set; } = new();
    public long AddResult { get; set; } = 1;
    public Exception? GetTorrentsException { get; set; }

    public Dictionary<string, bool>? LastFilter { get; private set; }
    public int GetTorrentsCalls { get; private set; }

    public List<(string Id, TorrentFileCleanup Cleanup)> Removed { get; } = [];
    public List<string[]> RemovedBatches { get; } = [];
    public List<string> Paused { get; } = [];
    public List<string> Resumed { get; } = [];
    public int PauseAllCalls { get; private set; }
    public int ResumeAllCalls { get; private set; }
    public int AddBytesCalls { get; private set; }
    public List<string> AddedUris { get; } = [];

    public List<(int? Up, bool? UpEnabled, int? Down, bool? DownEnabled, int? MaxDl, int? MaxSeeds)>
        SessionLimitCalls { get; } = [];

    public override Task<List<TransmissionTorrent>> GetTorrents(
        string[]? fields = null,
        TransmissionTorrentSort? sort = null,
        bool descending = false,
        Dictionary<string, bool>? filter = null,
        CancellationToken ct = default)
    {
        GetTorrentsCalls++;
        LastFilter = filter;
        if (GetTorrentsException is not null)
            throw GetTorrentsException;
        return Task.FromResult(Torrents);
    }

    public override Task<TransmissionSession> GetSession(CancellationToken ct = default) => Task.FromResult(Session);

    public override Task<TransmissionSessionStats> GetSessionStats(CancellationToken ct = default) => Task.FromResult(Stats);

    public override Task<long> AddTorrent(
        byte[] torrentData,
        string? downloadDir = null,
        string[]? labels = null,
        CancellationToken ct = default)
    {
        AddBytesCalls++;
        return Task.FromResult(AddResult);
    }

    public override Task<long> AddTorrentByUri(
        Uri uri,
        string? downloadDir = null,
        string[]? labels = null,
        CancellationToken ct = default)
    {
        AddedUris.Add(uri.ToString());
        return Task.FromResult(AddResult);
    }

    public override Task RemoveTorrent(
        string id,
        TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles,
        CancellationToken ct = default)
    {
        Removed.Add((id, fileCleanup));
        return Task.CompletedTask;
    }

    public override Task RemoveTorrents(
        string[] ids,
        TorrentFileCleanup fileCleanup = TorrentFileCleanup.DeleteFiles,
        CancellationToken ct = default)
    {
        RemovedBatches.Add(ids);
        return Task.CompletedTask;
    }

    public override Task PauseTorrent(string id, CancellationToken ct = default)
    {
        Paused.Add(id);
        return Task.CompletedTask;
    }

    public override Task ResumeTorrent(string id, CancellationToken ct = default)
    {
        Resumed.Add(id);
        return Task.CompletedTask;
    }

    public override Task PauseAllTorrents(CancellationToken ct = default)
    {
        PauseAllCalls++;
        return Task.CompletedTask;
    }

    public override Task ResumeAllTorrents(CancellationToken ct = default)
    {
        ResumeAllCalls++;
        return Task.CompletedTask;
    }

    public override Task<TransmissionSession> SetSessionLimits(
        int? uploadLimit = null,
        bool? uploadLimitEnabled = null,
        int? downloadLimit = null,
        bool? downloadLimitEnabled = null,
        int? maxActiveDownloads = null,
        int? maxActiveSeeds = null,
        CancellationToken ct = default)
    {
        SessionLimitCalls.Add((uploadLimit, uploadLimitEnabled, downloadLimit, downloadLimitEnabled,
            maxActiveDownloads, maxActiveSeeds));
        return Task.FromResult(Session);
    }
}
