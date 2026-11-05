using Stackarr.Media.Abstractions;

namespace Stackarr.Core.Events;

/// <summary>
/// Raised when media is removed from a library. Dependent features react to it, notably the
/// request system cancels any approved request for the title so it becomes requestable again.
/// </summary>
public sealed record MediaRemovedEvent(
    MediaDomain Domain,
    IReadOnlyDictionary<string, string> ProviderIds,
    string Title);
