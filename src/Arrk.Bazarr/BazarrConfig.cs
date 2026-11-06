using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace Arrk.Bazarr;

/// <summary>Settings for the optional Bazarr subtitle provider.</summary>
public class BazarrConfig
{
    [Description("Enable the Bazarr provider and its container")]
    [Display(Name = "Enabled")]
    public bool Enabled { get; set; }

    [Description("Expose Bazarr through the reverse proxy")]
    public bool Proxy { get; set; }

    [Description("Subtitle providers to enable in Bazarr")]
    [Display(Name = "Providers")]
    public List<string> Providers { get; set; } =
    [
        "gestdown",
        "subf2m"
    ];
}
