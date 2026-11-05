namespace Stackarr.Media.Abstractions;

public static class Resolutions
{
    public const string R2160p = "2160p";
    public const string R1080p = "1080p";
    public const string R720p = "720p";
    public const string R480p = "480p";

    /// <summary>
    /// Normalises common resolution labels to a canonical tier:
    /// <c>2160p</c> (4K/UHD), <c>1080p</c> (FHD), <c>720p</c> (HD) or <c>480p</c> (SD).
    /// The resolution wins when supplied; the quality name is the fallback.
    /// </summary>
    public static string Normalize(string? resolution, string? qualityName = null)
    {
        string fromResolution = NormalizeToken(resolution);
        if (fromResolution.Length > 0)
            return fromResolution;

        return NormalizeToken(qualityName);
    }

    static string NormalizeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        string lower = value.ToLowerInvariant().Replace("_", " ").Replace("-", " ");
        bool Has(string token) => lower.Contains(token, StringComparison.Ordinal);

        // 2160 first: "uhd" contains "hd", so the broader checks must come after the specific ones.
        if (Has("2160") || Has("4k") || Has("uhd") || Has("ultra hd") || Has("ultrahd"))
            return R2160p;
        if (Has("1080") || Has("fhd") || Has("full hd") || Has("fullhd"))
            return R1080p;
        if (Has("720") || Has("hd"))
            return R720p;
        if (Has("480") || Has("sd"))
            return R480p;
        return "";
    }

    public static string DisplayName(string resolution) => Normalize(resolution) switch
    {
        R2160p => "4K",
        R1080p => "1080p",
        R720p => "720p",
        R480p => "480p",
        _ => resolution
    };
}

public static class QualitySources
{
    public const string Bluray = "Bluray";
    public const string Web = "Web";
    public const string DVD = "DVD";
    public const string Remux = "Remux";
    public const string HDTV = "HDTV";

    public static QualitySource Parse(string? source, string? qualityName = null)
    {
        string combined = $"{source} {qualityName}".ToLowerInvariant();
        if (combined.Contains("bluray") || combined.Contains("blu-ray"))
            return QualitySource.Bluray;
        if (combined.Contains("remux"))
            return QualitySource.Remux;
        if (combined.Contains("web") || combined.Contains("webdl") || combined.Contains("webrip"))
            return QualitySource.Web;
        if (combined.Contains("dvd"))
            return QualitySource.DVD;
        if (combined.Contains("hdtv"))
            return QualitySource.HDTV;
        return QualitySource.Unknown;
    }
}

public static class VideoCodecs
{
    public const string x265 = "x265";
    public const string x264 = "x264";
    public const string AV1 = "AV1";

    public static string Normalize(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return "";
        string lower = codec.ToLowerInvariant();
        if (lower.Contains("x265") || lower.Contains("h265") || lower.Contains("hevc"))
            return x265;
        if (lower.Contains("x264") || lower.Contains("h264"))
            return x264;
        if (lower.Contains("av1"))
            return AV1;
        return codec;
    }
}

public static class AudioCodecs
{
    public const string TrueHD = "TrueHD";
    public const string Atmos = "Atmos";
    public const string DTSX = "DTS:X";
    public const string DTSHDMA = "DTS-HD MA";
    public const string DTSHD = "DTS-HD";
    public const string AAC = "AAC";
    public const string FLAC = "FLAC";
    public const string Opus = "Opus";
    public const string AC3 = "AC3";

    public static string Normalize(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return "";
        string lower = codec.ToLowerInvariant();
        if (lower.Contains("truehd"))
            return TrueHD;
        if (lower.Contains("atmos"))
            return Atmos;
        if (lower.Contains("dts-x") || lower.Contains("dts:x") || lower.Contains("dts x"))
            return DTSX;
        if (lower.Contains("dts-hd ma") || lower.Contains("dts hd ma"))
            return DTSHDMA;
        if (lower.Contains("dts-hd") || lower.Contains("dts hd"))
            return DTSHD;
        if (lower.Contains("aac"))
            return AAC;
        if (lower.Contains("flac"))
            return FLAC;
        if (lower.Contains("opus"))
            return Opus;
        if (lower.Contains("ac3") || lower.Contains("dolby digital"))
            return AC3;
        return codec;
    }
}

public static class HdrTypes
{
    public const string DolbyVision = "Dolby Vision";
    public const string HDR10Plus = "HDR10+";
    public const string HDR10 = "HDR10";

    public static QualityHDR Parse(string? hdrType, string? qualityName = null)
    {
        string combined = $"{hdrType} {qualityName}".ToLowerInvariant();
        if (combined.Contains("dolby vision") || combined.Contains("dv"))
        {
            if (combined.Contains("hdr10+") || combined.Contains("hdr10 plus"))
                return QualityHDR.HDR10PlusDV;
            return QualityHDR.DolbyVision;
        }
        if (combined.Contains("hdr10+") || combined.Contains("hdr10 plus"))
            return QualityHDR.HDR10Plus;
        if (combined.Contains("hdr"))
            return QualityHDR.HDR10;
        return QualityHDR.None;
    }
}
