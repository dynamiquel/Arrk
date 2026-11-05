using Stackarr.Ui;

namespace Stackarr.Jellyfin;

/// <summary>
/// The Jellyfin module's own script: the in-site web player. Served from
/// <c>_content/Stackarr.Jellyfin/js/stackarr-player.js</c>. The player page and its bare-video
/// layout live in the module too, so switching Jellyfin off removes the whole surface.
/// </summary>
public sealed class JellyfinHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.Jellyfin/js/stackarr-player.js", HeadAssetKind.Script, Order: 150)
    ];
}
