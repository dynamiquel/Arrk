namespace Arrk.Media.Abstractions;

public class QualityInfo
{
    public string Resolution { get; init; } = "";
    public QualitySource Source { get; init; }
    public string VideoCodec { get; init; } = "";
    public string AudioCodec { get; init; } = "";
    public QualityHDR HDR { get; init; }
    public string RawQuality { get; init; } = "";
    public string DisplayText { get; init; } = "";

}

public enum QualitySource
{
    Unknown,
    DVD,
    HDTV,
    Web,
    Bluray,
    Remux
}

public enum QualityHDR
{
    None,
    HDR10,
    HDR10Plus,
    DolbyVision,
    HDR10PlusDV
}

public class MediaFileInfo
{
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime DateAdded { get; init; }
    public QualityInfo Quality { get; init; } = new();

    /// <summary>Detailed stream metadata for the file (container, video/audio/subtitle streams).</summary>
    public MediaTechnicalInfo? Technical { get; init; }
}
