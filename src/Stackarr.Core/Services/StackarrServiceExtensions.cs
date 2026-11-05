namespace Stackarr.Core.Services;

public static class StackarrServiceExtensions
{
    public static IReadOnlyDictionary<string, string> GetDefaultEnvironment() =>
        new Dictionary<string, string>
        {
            ["PUID"] = "1000",
            ["PGID"] = "1000",
            ["TZ"] = "Etc/UTC"
        };
}
