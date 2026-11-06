using MudBlazor;
using Arrk.Core.Models;
using Arrk.Slskd;
using Arrk.Ui.Components;

namespace Arrk.DownloadManagement.Components.Shared;

/// <summary>Where a download came from. The user doesn't need to care beyond a small label.</summary>
public enum DownloadSource
{
    Torrent,
    Soulseek
}

/// <summary>
/// One row on the unified Downloads page. Torrents (Transmission) and Soulseek (slskd) transfers
/// are mapped into this single shape so the page can list, filter and group them together while
/// still labelling the source.
/// </summary>
public sealed record DownloadItem
{
    public DownloadSource Source { get; init; }
    public string Id { get; init; } = "";
    public string Username { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    /// <summary>0–100.</summary>
    public double Progress { get; init; }
    public string State { get; init; } = "";
    public string StateVariant { get; init; } = "neutral";
    public StatusGroupKind StateKind { get; init; } = StatusGroupKind.Other;
    public long Size { get; init; }
    public long Downloaded { get; init; }
    public double DownloadSpeed { get; init; }
    public double UploadSpeed { get; init; }
    public double Ratio { get; init; }
    public int Seeds { get; init; }
    public int Peers { get; init; }
    public string RemainingTime { get; init; } = "";
    public DateTime AddedOn { get; init; }
    /// <summary>Secondary meta line (Soulseek peer, or an error message).</summary>
    public string Meta { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? PenaltyLabel { get; init; }

    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public bool CanDelete { get; init; }
    public bool CanCancel { get; init; }

    public bool IsTorrent => Source == DownloadSource.Torrent;
    public string SourceLabel => Source == DownloadSource.Soulseek ? "Soulseek" : "Torrent";
    public string SourceIcon => Source == DownloadSource.Soulseek
        ? Icons.Material.Filled.MusicNote
        : Icons.Material.Filled.CloudDownload;
    public string SourceVariant => Source == DownloadSource.Soulseek ? "purple" : "info";
    public double Percent => Math.Clamp(Progress, 0, 100);

    /// <summary>Display percentage, floored to one decimal so an incomplete 99.97% never reads 100%.</summary>
    public double PercentDisplay => Math.Floor(Percent * 10) / 10;

    public static DownloadItem FromTorrent(TorrentItem t) => new()
    {
        Source = DownloadSource.Torrent,
        Id = t.Hash,
        Name = t.Name,
        Category = t.Category ?? "",
        Progress = t.Progress * 100,
        State = t.State.ToString(),
        StateVariant = t.State switch
        {
            TorrentState.Downloading => "info",
            TorrentState.Seeding => "success",
            TorrentState.Paused => "neutral",
            TorrentState.Error => "error",
            _ => "neutral"
        },
        StateKind = DownloadStatusGrouping.GroupOfTorrent(t),
        Size = t.Size,
        Downloaded = t.Downloaded,
        DownloadSpeed = t.DownloadSpeed,
        UploadSpeed = t.UploadSpeed,
        Ratio = t.Ratio,
        Seeds = t.Seeds,
        Peers = t.Peers,
        RemainingTime = t.RemainingTime,
        AddedOn = t.AddedOn,
        CanPause = t.State is TorrentState.Downloading or TorrentState.Seeding,
        CanResume = t.State == TorrentState.Paused,
        // Torrents use the same "Cancel" affordance as Soulseek (removes the torrent and its files).
        CanCancel = true,
        CanDelete = false
    };

    public static DownloadItem FromSoulseek(SlskdDownload d) => new()
    {
        Source = DownloadSource.Soulseek,
        Id = d.Id,
        Username = d.Username,
        Name = string.IsNullOrWhiteSpace(d.FileName) ? d.Filename : d.FileName,
        Progress = d.PercentComplete,
        State = d.IsComplete ? "Completed" : d.IsFailed ? "Failed" : d.IsQueued ? "Queued" : "Downloading",
        StateVariant = d.IsComplete ? "success" : d.IsFailed ? "error" : d.IsQueued ? "neutral" : "info",
        StateKind = d.IsComplete ? StatusGroupKind.Downloaded
            : d.IsFailed ? StatusGroupKind.Error
            : d.IsQueued ? StatusGroupKind.Queued
            : StatusGroupKind.Downloading,
        Size = d.Size,
        Downloaded = d.BytesTransferred,
        AddedOn = d.EnqueuedAt ?? DateTime.MinValue,
        Meta = d.Username,
        Detail = !string.IsNullOrWhiteSpace(d.Exception) ? d.Exception
            : !string.IsNullOrWhiteSpace(d.DirectoryName) ? d.DirectoryName : d.Directory,
        CanCancel = !d.IsComplete && !d.IsFailed
    };
}
