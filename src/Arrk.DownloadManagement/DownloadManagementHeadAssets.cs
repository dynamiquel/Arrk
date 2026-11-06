using Arrk.Ui;

namespace Arrk.DownloadManagement;

/// <summary>
/// The download-management module's own stylesheet (the unified torrent/Soulseek cards and the
/// mobile torrent layout), served from <c>_content/Arrk.DownloadManagement/css/downloads.css</c>.
/// </summary>
public sealed class DownloadManagementHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Arrk.DownloadManagement/css/downloads.css", HeadAssetKind.Style, Order: 132)
    ];
}
