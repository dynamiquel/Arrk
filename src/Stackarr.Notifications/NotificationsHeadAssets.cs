using Stackarr.Ui;

namespace Stackarr.Notifications;

/// <summary>
/// The notifications module's own stylesheet (the app-bar bell, inbox card sheet, live activity and
/// the event feed), served from <c>_content/Stackarr.Notifications/css/notifications.css</c>.
/// </summary>
public sealed class NotificationsHeadAssets : IHeadAssetProvider
{
    public IReadOnlyList<HeadAsset> Assets { get; } =
    [
        new("_content/Stackarr.Notifications/css/notifications.css", HeadAssetKind.Style, Order: 131)
    ];
}
