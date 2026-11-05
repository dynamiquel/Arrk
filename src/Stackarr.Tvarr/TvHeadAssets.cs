using Stackarr.Ui;

namespace Stackarr.Tvarr;

/// <summary>Styles and the streaming upload helper owned by the TV module.</summary>
public sealed class TvHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.Tvarr/css/tv-upload.css", HeadAssetKind.Style, Order: 130),
        new("_content/Stackarr.Tvarr/js/stackarr-tv-upload.js", HeadAssetKind.Script, Order: 155)
    ];
}
