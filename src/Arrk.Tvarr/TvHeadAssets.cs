using Arrk.Ui;

namespace Arrk.Tvarr;

/// <summary>Styles and the streaming upload helper owned by the TV module.</summary>
public sealed class TvHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Arrk.Tvarr/css/tv-upload.css", HeadAssetKind.Style, Order: 130),
        new("_content/Arrk.Tvarr/js/arrk-tv-upload.js", HeadAssetKind.Script, Order: 155)
    ];
}
