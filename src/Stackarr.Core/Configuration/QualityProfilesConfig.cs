using System.ComponentModel;
using Stackarr.Core.Models;

namespace Stackarr.Core.Configuration;

public class QualityProfilesConfig
{
    [Description("Quality profiles available for content selection")]
    public List<QualityProfileDefinition> Profiles { get; set; } = [..DefaultProfiles];

    [Description("Shared preferred codec scoring (applied to all profiles)")]
    public CodecPreferences Preferred { get; set; } = new();

    [Description(
        "Minimum bitrate in Mbps per resolution tier. A release below its tier's floor is " +
        "rejected, so a 4K download cannot be smaller than a good 1080p one. Set 0 to leave a " +
        "tier unmanaged.")]
    public Dictionary<string, double> MinimumBitratesMbps { get; set; } = new()
    {
        ["1080p"] = 3,
        ["2160p"] = 12
    };

    static List<QualityProfileDefinition> DefaultProfiles =>
    [
        new() { Name = "Best Available", MinResolution = "1080p", MaxResolution = "2160p", AllowUpgrade = true },
        new() { Name = "FHD", MinResolution = "1080p", MaxResolution = "1080p", AllowUpgrade = true },
        new() { Name = "UHD", MinResolution = "2160p", MaxResolution = "2160p", AllowUpgrade = true }
    ];
}

public class QualityProfileDefinition
{
    [Description("Profile name (must be unique)")]
    public string Name { get; set; } = "";

    [Description("Allowed quality types: TV, DVD, Web, Bluray, Remux")]
    public List<QualitySource> AllowedQualities { get; set; } = [QualitySource.Web, QualitySource.Bluray, QualitySource.Remux];

    [Description("Minimum resolution to accept")]
    public string MinResolution { get; set; } = "1080p";

    [Description("Maximum resolution / cutoff point")]
    public string MaxResolution { get; set; } = "2160p";

    [Description("Allow upgrading within this profile's range")]
    public bool AllowUpgrade { get; set; } = true;

    [Description("Cutoff quality type, defaults to highest allowed type")]
    public QualitySource? CutoffQuality { get; set; }
}

public class CodecPreferences
{
    [Description("Video codec preference scores (higher = preferred)")]
    public Dictionary<string, int> Video { get; set; } = new()
    {
        ["dolby_vision"] = 100,
        ["hdr10+"] = 90,
        ["hdr"] = 80,
        ["h265"] = 20,
        ["h264"] = 10
    };

    [Description("Audio codec preference scores (higher = preferred)")]
    public Dictionary<string, int> Audio { get; set; } = new()
    {
        ["dolby_truehd"] = 50,
        ["dolby_atmos"] = 45,
        ["dts_x"] = 40,
        ["dts_hd_ma"] = 35
    };
}
