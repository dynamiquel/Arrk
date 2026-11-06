using Arrk.Ui;

namespace Arrk.Subtitles;

public sealed class SubtitlesHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Arrk.Subtitles/css/subtitles.css", HeadAssetKind.Style, Order: 120)
    ];
}
