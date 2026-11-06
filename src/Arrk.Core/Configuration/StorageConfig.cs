using System.ComponentModel;

namespace Arrk.Core.Configuration;

public class StorageConfig
{
    [Description("Host path for service configuration files. Absolute = used verbatim as the Docker bind source; relative = anchored to the data route and mapped to the host.")]
    public string Config { get; set; } = "config";

    [Description("Host path for the download staging area. Absolute = used verbatim as the Docker bind source; relative = anchored to the data route and mapped to the host.")]
    public string Downloads { get; set; } = "downloads";

    [Description("Host path for the media storage root. Absolute = used verbatim as the Docker bind source; relative = anchored to the data route and mapped to the host.")]
    public string Media { get; set; } = "media";

    [Description("Relative path for TV shows under base")]
    public string Tv { get; set; } = "$media/tv";

    [Description("Relative path for music under base")]
    public string Music { get; set; } = "$media/music";
}
