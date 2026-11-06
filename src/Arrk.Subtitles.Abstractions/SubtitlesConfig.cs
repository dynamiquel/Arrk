using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Arrk.Subtitles.Abstractions;

/// <summary>Shared subtitle settings: languages, automatic downloads and dispatcher scoring.</summary>
public class SubtitlesConfig
{
    [Description("Enable subtitle management and its UI")]
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; } = true;

    [Description("Subtitle languages to download")]
    [Display(Name = "Default languages")]
    public List<string> Languages { get; set; } = ["en"];

    [Description("Download missing subtitles automatically for new imports")]
    [Display(Name = "Download automatically")]
    public bool AutoDownload { get; set; } = true;

    [Description("Minimum match score for an automatic download (0-100)")]
    [Range(0, 100)]
    [Display(Name = "Minimum score")]
    public int MinScore { get; set; } = 70;

    [Description("Maximum automatic downloads per run")]
    [Range(1, 100)]
    [Display(Name = "Maximum downloads per run")]
    public int MaxDownloadsPerRun { get; set; } = 10;

    public SubtitlePreferences ToPreferences() => new(
        SubtitleLanguages.NormalizeTwoLetter(Languages),
        [],
        AutoDownload);
}
