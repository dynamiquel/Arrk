namespace Stackarr.Tv.Abstractions;

/// <summary>The TMDB identity of a TV/movie item, when it has one.</summary>
public interface ITmdbIdentity
{
    int TmdbId { get; }
}

/// <summary>The TVDB identity of a TV item, when it has one.</summary>
public interface ITvdbIdentity
{
    int TvdbId { get; }
}

/// <summary>The IMDB identity of a TV/movie item, when it has one.</summary>
public interface IImdbIdentity
{
    string ImdbId { get; }
}

/// <summary>Provider-id keys used by TV requests, watchlists and release searches.</summary>
public static class TvProviderIds
{
    public const string Tmdb = "tmdb";
    public const string Tvdb = "tvdb";
    public const string Imdb = "imdb";
}
