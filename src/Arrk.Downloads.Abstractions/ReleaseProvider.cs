
namespace Arrk.Downloads.Abstractions;

/// <summary>A search-only release source: it can list candidates but not grab them.</summary>
public interface IReleaseSearchSource
{
    string Name { get; }

    Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        ReleaseSearchQuery query, CancellationToken ct = default);

    IAsyncEnumerable<ReleaseCandidate> SearchStreamAsync(
        ReleaseSearchQuery query, CancellationToken ct = default);
}

/// <summary>A source that can hand a chosen candidate to its downloader/importer.</summary>
public interface IReleaseGrabber
{
    Task<ReleaseGrabResult> GrabAsync(
        ReleaseCandidate release, ReleaseSearchQuery query, CancellationToken ct = default);
}

/// <summary>
/// A source of downloadable releases for the interactive search UI. The shared picker talks only
/// to this contract; each media domain supplies a concrete provider (Radarr, Sonarr, music via
/// Prowlarr, and later Soulseek/slskd). Providers are DI singletons and hold no per-request state.
///
/// This is the only seam between the UI and the media modules: adding a new source
/// (e.g. Soulseek) is a new provider, with no changes to the picker or the dialogs that host it.
/// </summary>
public abstract class ReleaseProvider : IReleaseSearchSource, IReleaseGrabber
{
    /// <summary>Short source name shown in the picker (e.g. "Radarr", "Prowlarr").</summary>
    public abstract string Name { get; }

    /// <summary>Searches all indexers for the query and returns scored, normalised candidates.</summary>
    public abstract Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        ReleaseSearchQuery query, CancellationToken ct = default);

    /// <summary>
    /// Streams candidates as each source/indexer returns them, so the picker can show results
    /// immediately instead of waiting for the slowest indexer. The default implementation wraps
    /// <see cref="SearchAsync"/>; providers backed by a single upstream call override it only when
    /// they can genuinely stream.
    /// </summary>
    public virtual async IAsyncEnumerable<ReleaseCandidate> SearchStreamAsync(
        ReleaseSearchQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        IReadOnlyList<ReleaseCandidate> results = await SearchAsync(query, ct);
        foreach (ReleaseCandidate release in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return release;
        }
    }

    /// <summary>Grabs the chosen candidate, handing it to the owning downloader/importer.</summary>
    public abstract Task<ReleaseGrabResult> GrabAsync(
        ReleaseCandidate release, ReleaseSearchQuery query, CancellationToken ct = default);
}
