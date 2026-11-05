using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Stackarr.Navidrome;

public class NavidromeConfig
{
    [Description("Enable Navidrome music server")]
    public bool Enabled { get; set; } = true;

    [Description("Expose Navidrome through the reverse proxy at /navidrome")]
    public bool Proxy { get; set; } = true;

    [Description("Navidrome username Stackarr uses for the Subsonic API (library browsing, playlists and playback)")]
    public string ApiUsername { get; set; } = "stackarr";

    [Description("Password for the Stackarr Navidrome API account")]
    [DataType(DataType.Password)]
    public string ApiPassword { get; set; } = "";

    [Description("Enable the in-site music player (streams through Stackarr so no Navidrome login is needed)")]
    public bool WebPlayer { get; set; } = true;
}
