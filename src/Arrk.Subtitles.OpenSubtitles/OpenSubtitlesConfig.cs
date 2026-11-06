using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Arrk.Subtitles.OpenSubtitles;

/// <summary>Settings for the built-in OpenSubtitles backend.</summary>
public class OpenSubtitlesConfig
{
    [Description("Enable the OpenSubtitles provider")]
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; } = true;

    [Description("OpenSubtitles.com API consumer key. Register one under Profile -> API Consumers on opensubtitles.com.")]
    [DataType(DataType.Password)]
    [Display(Name = "API key")]
    public string? ApiKey { get; set; }

    [Description("OpenSubtitles.com username, not email (optional; raises the free download quota)")]
    [Display(Name = "OpenSubtitles username")]
    public string? Username { get; set; }

    [Description("OpenSubtitles.com password (optional)")]
    [DataType(DataType.Password)]
    [Display(Name = "OpenSubtitles password")]
    public string? Password { get; set; }

    [Description("Include machine-translated and AI-translated subtitles")]
    [Display(Name = "Include machine translations")]
    public bool IncludeMachineTranslated { get; set; }
}
