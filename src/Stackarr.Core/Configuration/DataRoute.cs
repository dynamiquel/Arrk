using System.Runtime.InteropServices;

namespace Stackarr.Core.Configuration;

/// <summary>
/// Resolves the "data route", the root directory that Stackarr's own files (config, database)
/// and default storage roots (config/downloads/media) are anchored to.
/// <para>
/// The route is taken from the <c>STACKARR_DATA_ROUTE</c> environment variable when set.
/// When unset it defaults to the current working directory (a relative root), so a standalone
/// run uses <c>./config</c>, <c>./downloads</c>, etc. The shipped container sets
/// <c>STACKARR_DATA_ROUTE=/</c> so paths resolve to <c>/config</c>, <c>/downloads</c>, etc.
/// </para>
/// <para>
/// Users can override the route with the environment variable, or override the individual
/// storage paths in the YAML config.
/// </para>
/// </summary>
public static class DataRoute
{
    public const string EnvironmentVariable = "STACKARR_DATA_ROUTE";

    /// <summary>
    /// The configured or default data route, normalized to an absolute path.
    /// </summary>
    public static string Root => Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// Resolve a data route value to an absolute path. An empty or whitespace value falls back
    /// to the current working directory. Tilde prefixes are expanded to the user profile.
    /// </summary>
    public static string Resolve(string? configuredRoute)
    {
        string route = configuredRoute?.Trim() ?? "";

        if (route.Length == 0)
            route = ".";

        if (route == "~" || route.StartsWith("~/", StringComparison.Ordinal) ||
            route.StartsWith("~\\", StringComparison.Ordinal))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            route = route.Length <= 2 ? home : Path.Combine(home, route[2..]);
        }

        return Path.GetFullPath(route);
    }

    /// <summary>
    /// Resolves a path that may be relative, anchoring it to <paramref name="root"/>.
    /// Absolute paths (including Windows drive paths) are returned unchanged.
    /// </summary>
    public static string ResolveUnder(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return root;

        return Path.IsPathRooted(path) ? path : Path.Combine(root, path);
    }
}
