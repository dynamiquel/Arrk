namespace Stackarr.Core.Models;

/// <summary>
/// File names and relative locations of Stackarr's own files. These are relative, the absolute location is determined by the configured data route
/// (see <see cref="Stackarr.Core.Configuration.DataRoute"/>). In the shipped container the
/// data route is <c>/</c>, so the config file lives at <c>/config/stackarr.yaml</c>; a
/// standalone run defaults to the current working directory so it lives at
/// <c>./config/stackarr.yaml</c>.
/// </summary>
public static class StackarrFiles
{
    /// <summary>Directory (relative to the data route) holding Stackarr's own files.</summary>
    public const string ConfigDirectoryName = "config";

    /// <summary>Directory (relative to the data route) where drop-in plugin DLLs are loaded from.</summary>
    public const string PluginsDirectoryName = "plugins";

    public const string ConfigFileName = "stackarr.yaml";
    public const string ConfigJsonFileName = "stackarr.json";
    public const string DatabaseFileName = "stackarr.db";
    public const string HangfireDatabaseFileName = "Hangfire.db";
    public const string VersionFileNamePrefix = "stackarr_";

    /// <summary>Directory holding Stackarr's own files, relative to the data route.</summary>
    public static string ConfigDirectory(string dataRoot) =>
        System.IO.Path.Combine(dataRoot, ConfigDirectoryName);

    /// <summary>Absolute path to the YAML config file under the given data root.</summary>
    public static string ConfigPath(string dataRoot) =>
        System.IO.Path.Combine(ConfigDirectory(dataRoot), ConfigFileName);

    /// <summary>Absolute path to the JSON config file under the given data root.</summary>
    public static string ConfigJsonPath(string dataRoot) =>
        System.IO.Path.Combine(ConfigDirectory(dataRoot), ConfigJsonFileName);

    /// <summary>Absolute path to the SQLite database file under the given data root.</summary>
    public static string DatabasePath(string dataRoot) =>
        System.IO.Path.Combine(ConfigDirectory(dataRoot), DatabaseFileName);

    /// <summary>Absolute path to the Hangfire job database file under the given data root.</summary>
    public static string HangfireDatabasePath(string dataRoot) =>
        System.IO.Path.Combine(ConfigDirectory(dataRoot), HangfireDatabaseFileName);

    /// <summary>
    /// Directory (relative to the data route) that drop-in plugin assemblies and their static
    /// assets are loaded/served from. A runtime-loaded plugin cannot use <c>_content</c>
    /// (its static web-asset manifest is not part of the host), so its assets live here and are
    /// served from <c>/plugins/...</c>.
    /// </summary>
    public static string PluginsDirectory(string dataRoot) =>
        System.IO.Path.Combine(dataRoot, PluginsDirectoryName);
}
