using System.Runtime.InteropServices;

namespace Arrk.Core.Configuration;

/// <summary>
/// Resolves the "data route", the root directory that Arrk's own files (config, database)
/// and default storage roots (config/downloads/media) are anchored to.
/// <para>
/// The route is taken from the <c>ARRK_DATA_ROUTE</c> environment variable when set.
/// When unset, the historical <c>STACKARR_DATA_ROUTE</c> remains supported, then the current
/// working directory is used. A standalone run uses <c>./config</c>, <c>./downloads</c>, etc.
/// The shipped container defaults <c>STACKARR_DATA_ROUTE=/</c> so old overrides still work;
/// <c>ARRK_DATA_ROUTE</c> takes precedence when explicitly set.
/// </para>
/// <para>
/// Users can override the route with the environment variable, or override the individual
/// storage paths in the YAML config.
/// </para>
/// </summary>
public static class DataRoute
{
    public const string EnvironmentVariable = "ARRK_DATA_ROUTE";

    /// <summary>
    /// The configured or default data route, normalized to an absolute path.
    /// </summary>
    public static string Root => Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable)
        ?? Environment.GetEnvironmentVariable("STACKARR_DATA_ROUTE"));

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
