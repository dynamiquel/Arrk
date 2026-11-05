using System.ComponentModel;

namespace Stackarr.Core.Configuration;

public class QualitySettings
{
    [Description("Minimum acceptable quality (e.g. 1080p, 4k)")]
    public string Min { get; set; } = "1080p";

    [Description("Automatically upgrade to better quality when available")]
    public bool Upgrade { get; set; } = true;

    [Description("Maximum quality to upgrade to")]
    public string UpgradeTo { get; set; } = "4k";

    [Description("Preferred quality scoring")]
    public CodecPreferences Codecs { get; set; } = new();
}
