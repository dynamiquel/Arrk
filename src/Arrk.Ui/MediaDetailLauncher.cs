namespace Arrk.Ui;

/// <summary>
/// Identifies a player item whose movie/series entry should be shown. The player module only knows
/// the player's opaque item id; the media module resolves it to its own library model.
/// </summary>
public sealed record MediaDetailRequest(string ItemId);

/// <summary>
/// Lets a player module ask the media module to open its detail dialog, without the player
/// referencing the TV module (the dependency runs the other way). The media module contributes a
/// component to <see cref="UiRegions.PlayerOverlays"/> that handles the request.
/// </summary>
public sealed class MediaDetailLauncher
{
    /// <summary>Raised when the player title is clicked. Null when no media module is available.</summary>
    public event Func<MediaDetailRequest, Task>? Requested;

    public bool IsAvailable => Requested is not null;

    public Task ShowAsync(MediaDetailRequest request) =>
        Requested?.Invoke(request) ?? Task.CompletedTask;
}
