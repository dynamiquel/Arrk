namespace Arrk.Tvarr.Models;

/// <summary>
/// What Arrk managed to do when auto-resolving a torrent that was removed because it failed
/// (stalled, slow or errored). Used for logging and to decide whether the pending cleanup is done.
/// </summary>
/// <param name="Found">The torrent was traced back to a queue item or history record in the arr.</param>
/// <param name="Blocklisted">The release was blocklisted so it will not be grabbed again.</param>
/// <param name="SearchTriggered">A fresh search was triggered for the owning movie/episode.</param>
/// <param name="Detail">Human-readable description for logs.</param>
public record FailedDownloadResolution(
    bool Found,
    bool Blocklisted,
    bool SearchTriggered,
    string Detail);
