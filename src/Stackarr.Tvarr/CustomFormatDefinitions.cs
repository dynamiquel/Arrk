namespace Stackarr.Tvarr;

public static class CustomFormatDefinitions
{
    public static readonly List<CustomFormatSpec> VideoFormats =
    [
        new("Dolby Vision", [new(@"DoVi|\.dv\.|DVDRip")]),
        new("HDR10+", [new(@"HDR10\+|HDR10P|\.HDR\+")]),
        new("HDR10", [new(@"HDR(?!10|\+)|(?<!HDR10\+)HDR10(?!\+)")]),
        new("HLG", [new(@"\bHLG\b")])
    ];

    public static readonly List<CustomFormatSpec> AudioFormats =
    [
        new("Dolby TrueHD", [new("True[ .-]?HD(?!.*Atmos)")]),
        new("Dolby Atmos", [new("\\bATMOS(\\b|\\d)")]),
        new("DTS-X", [new("DTS[ .-]?X(?!\\d)")]),
        new("DTS-HD MA", [new("DTS[ .-]?HD[ .-]?MA")])
    ];
}

public record CustomFormatSpec(string Name, List<ReleaseTitleSpecification> Specifications);
public record ReleaseTitleSpecification(string Pattern);
