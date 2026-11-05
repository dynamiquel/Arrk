namespace Stackarr.Ui;

/// <summary>
/// A section a module contributes to the Discover page. The shell renders one tab per source and
/// hosts the source's component, so a module can add (or lose) a discovery surface without the
/// shell referencing it. <see cref="Id"/> is both the tab identity and the URL segment
/// (<c>/discover/{Id}</c>), and must be unique across modules.
/// </summary>
public sealed record DiscoverSource(
    string Id,
    string Label,
    string Icon,
    Type Component,
    int Order = 100,
    string? EnabledConfigPath = null,
    IReadOnlyDictionary<string, object?>? Parameters = null);

/// <summary>Contributes discovery sections to the shell's Discover page. Modules register it in DI.</summary>
public interface IDiscoverSourceProvider
{
    IReadOnlyList<DiscoverSource> Sources { get; }
}
