namespace Stackarr.Tvarr.Services;

/// <summary>
/// Maps arr container roots to Stackarr-local roots, longest prefix first. Falls back to the
/// original path when it already resolves locally (standalone development). Roots can be supplied
/// directly or through a factory so a storage-path config change is reflected without a restart.
/// </summary>
public sealed class ArrMediaPathMapper : IMediaPathMapper
{
    readonly Func<IEnumerable<(string Container, string Local)>>? _rootsFactory;
    readonly IReadOnlyList<(string Container, string Local)>? _roots;

    public ArrMediaPathMapper(IEnumerable<(string Container, string Local)> roots)
        => _roots = Normalize(roots);

    /// <summary>
    /// Live constructor: the roots are re-resolved on every mapping so changing
    /// <c>storage.media</c>/<c>storage.tv</c> applies without an app restart.
    /// </summary>
    public ArrMediaPathMapper(Func<IEnumerable<(string Container, string Local)>> rootsFactory)
        => _rootsFactory = rootsFactory;

    IReadOnlyList<(string Container, string Local)> CurrentRoots =>
        _roots ?? Normalize(_rootsFactory!());

    public string? MapToLocalPath(string arrPath)
    {
        if (string.IsNullOrWhiteSpace(arrPath))
            return null;

        string normalized = arrPath.Replace('\\', '/');

        foreach ((string container, string local) in CurrentRoots)
        {
            if (normalized.Equals(container, StringComparison.Ordinal))
                return local;

            if (normalized.StartsWith(container + "/", StringComparison.Ordinal))
                return local.TrimEnd('/') + normalized[container.Length..];
        }

        return File.Exists(arrPath) ? arrPath : null;
    }

    static IReadOnlyList<(string Container, string Local)> Normalize(IEnumerable<(string Container, string Local)> roots) =>
        roots
            .Where(root => !string.IsNullOrWhiteSpace(root.Container) && !string.IsNullOrWhiteSpace(root.Local))
            .Select(root => (Container: NormalizePath(root.Container), Local: root.Local.Replace('\\', '/')))
            .OrderByDescending(root => root.Container.Length)
            .ToList();

    static string NormalizePath(string path) => path.Replace('\\', '/').TrimEnd('/');
}
