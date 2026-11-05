namespace Stackarr.Core.Modules;

/// <summary>
/// Metadata about a loaded Stackarr module: its display name, version, author, description and
/// third-party dependencies (used by the About page and module discovery).
/// </summary>
public record ModuleInfo(
    string ModuleName,
    string ModuleVersion,
    string Author,
    string Description,
    IReadOnlyList<ModuleDependency> Dependencies);
