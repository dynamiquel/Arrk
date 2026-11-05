using Stackarr.Ui;

namespace Stackarr.Subtitles;

public sealed class SubtitlesHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.Subtitles/css/subtitles.css", HeadAssetKind.Style, Order: 120)
    ];
}
