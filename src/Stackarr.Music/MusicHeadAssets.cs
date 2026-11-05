using Stackarr.Ui;

namespace Stackarr.Music;

/// <summary>
/// The music module's own scripts and styles. The mini/full player, lyrics engine and playlist-icon
/// helpers are served from <c>_content/Stackarr.Music/...</c>; the shared theme and the reusable
/// component scripts come from <see cref="UiRegistry"/> itself. Load order matters: the lyrics engine
/// is registered before the player that drives it.
/// </summary>
public sealed class MusicHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.Music/css/music.css", HeadAssetKind.Style, Order: 120),
        new("_content/Stackarr.Music/js/stackarr-lyrics.js", HeadAssetKind.Script, Order: 160),
        new("_content/Stackarr.Music/js/stackarr-music.js", HeadAssetKind.Script, Order: 170)
    ];
}
