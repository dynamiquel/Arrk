using Stackarr.Media.Abstractions;

namespace Stackarr.Tv.Abstractions;

/// <summary>Media-domain keys owned by the TV/movie feature.</summary>
public static class TvMediaDomains
{
    public const string Movie = "movie";
    public const string Tv = "tv";

    public static MediaDomain MovieDomain => new(Movie);

    public static MediaDomain TvDomain => new(Tv);
}

/// <summary>Release-search kinds owned by the TV/movie feature.</summary>
public static class TvReleaseKinds
{
    public const string Movie = "movie";
    public const string Series = "series";
    public const string Season = "season";
    public const string Episode = "episode";
}
