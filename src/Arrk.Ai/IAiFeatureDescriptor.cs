namespace Arrk.Ai;

/// <summary>
/// Describes one AI-backed feature so the admin page can list everything that may spend tokens,
/// wherever its configuration lives. Modules contribute their own descriptors in
/// <c>ConfigureServices</c>; the core features are registered by <c>AddArrkAi</c>.
/// </summary>
public interface IAiFeatureDescriptor
{
    /// <summary>Stable key used on usage rows, e.g. <c>releaseRanking</c>.</summary>
    string Key { get; }

    string Name { get; }

    string Description { get; }

    /// <summary>Live effective state (the master switch and the feature's own toggle).</summary>
    bool IsEnabled { get; }
}
