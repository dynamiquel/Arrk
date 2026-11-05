using Stackarr.Core.Lifecycle;
using Stackarr.Core.Playback;
using Stackarr.Music.Models;

namespace Stackarr.Music.Services;

/// <summary>
/// Projects lifecycle status onto music cards/dialogs. Albums are the staleness unit; songs inherit
/// their album's status so a track row can show the same warning.
/// </summary>
public sealed class MusicStaleEnricher(StaleMediaService stale)
{
    public bool IsAvailable => stale.IsAvailable;

    public Task<IReadOnlyDictionary<string, StaleMediaItem>> LoadAsync(CancellationToken ct = default) =>
        stale.GetStatusesAsync(WatchActivityServices.Music, ct);

    public List<MusicItem> Enrich(IEnumerable<MusicItem> items, IReadOnlyDictionary<string, StaleMediaItem> statuses)
    {
        List<MusicItem> result = [];
        foreach (MusicItem item in items)
        {
            string key = item.EntityType switch
            {
                MusicEntityType.Album => MediaItemKeys.MusicAlbum(item.ArtistName, item.Title),
                MusicEntityType.Track => MediaItemKeys.MusicAlbum(item.ArtistName, item.AlbumName),
                _ => ""
            };

            result.Add(!string.IsNullOrEmpty(key) && statuses.TryGetValue(key, out StaleMediaItem? status)
                ? item with
                {
                    Staleness = status.Level,
                    DaysRemaining = status.DaysRemaining,
                    IsBookmarked = status.IsBookmarked,
                    BookmarkLabel = status.BookmarkLabel,
                    IsPinned = status.IsPinned
                }
                : item);
        }
        return result;
    }
}
