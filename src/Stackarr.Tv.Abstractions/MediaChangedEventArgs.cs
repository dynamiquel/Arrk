namespace Stackarr.Tv.Abstractions;

public class MoviesChangedEventArgs : EventArgs
{
    public IReadOnlyList<TvMovie> Movies { get; init; } = [];
}

public class SeriesChangedEventArgs : EventArgs
{
    public IReadOnlyList<TvSeries> Series { get; init; } = [];
}
