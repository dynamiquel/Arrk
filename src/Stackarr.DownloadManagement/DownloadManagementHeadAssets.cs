using Stackarr.Ui;

namespace Stackarr.DownloadManagement;

/// <summary>
/// The download-management module's own stylesheet (the unified torrent/Soulseek cards and the
/// mobile torrent layout), served from <c>_content/Stackarr.DownloadManagement/css/downloads.css</c>.
/// </summary>
public sealed class DownloadManagementHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.DownloadManagement/css/downloads.css", HeadAssetKind.Style, Order: 132)
    ];
}
