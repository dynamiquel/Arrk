using Stackarr.Media.Abstractions;

namespace Stackarr.Tvarr.Models;

/// <summary>Maps arr quality payloads onto the provider-agnostic <see cref="QualityInfo"/>.</summary>
public static class ArrQualityMapper
{
    public static QualityInfo FromRadarrQuality(
        string qualityName,
        string resolution = "",
        string videoCodec = "",
        string audioCodec = "",
        string hdrType = "",
        string source = "")
    {
        string resolved = Resolutions.Normalize(resolution, qualityName);
        QualitySource src = QualitySources.Parse(source, qualityName);
        QualityHDR hdr = HdrTypes.Parse(hdrType, qualityName);
        string vc = VideoCodecs.Normalize(videoCodec);
        string ac = AudioCodecs.Normalize(audioCodec);

        string display = BuildDisplayText(resolved, src, hdr, vc, ac);

        return new()
        {
            Resolution = resolved,
            Source = src,
            VideoCodec = vc,
            AudioCodec = ac,
            HDR = hdr,
            RawQuality = qualityName,
            DisplayText = display
        };
    }

    public static QualityInfo FromSonarrQuality(
        string qualityName,
        string resolution = "",
        string videoCodec = "",
        string audioCodec = "",
        string hdrType = "",
        string source = "")
    {
        return FromRadarrQuality(qualityName, resolution, videoCodec, audioCodec, hdrType, source);
    }

    static string BuildDisplayText(string resolution, QualitySource source, QualityHDR hdr, string videoCodec, string audioCodec)
    {
        List<string> parts = new List<string>();
        if (!string.IsNullOrEmpty(resolution))
            parts.Add(Resolutions.DisplayName(resolution));
        if (source != QualitySource.Unknown)
            parts.Add(source.ToString());
        if (hdr != QualityHDR.None)
        {
            if (hdr == QualityHDR.DolbyVision)
                parts.Add("DV");
            else if (hdr == QualityHDR.HDR10Plus)
                parts.Add("HDR10+");
            else if (hdr == QualityHDR.HDR10PlusDV)
            {
                parts.Add("DV");
                parts.Add("HDR10+");
            }
            else
                parts.Add("HDR");
        }
        if (!string.IsNullOrEmpty(videoCodec))
            parts.Add(videoCodec);
        if (!string.IsNullOrEmpty(audioCodec))
            parts.Add(audioCodec);
        return string.Join(" ", parts);
    }
}
