
namespace Stackarr.Music.Abstractions;

public class ArtistsChangedEventArgs : EventArgs
{
    public IReadOnlyList<Artist> Artists { get; init; } = [];
}

public class AlbumsChangedEventArgs : EventArgs
{
    public IReadOnlyList<Album> Albums { get; init; } = [];
}
