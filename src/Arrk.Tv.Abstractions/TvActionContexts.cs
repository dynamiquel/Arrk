namespace Arrk.Tv.Abstractions;

/// <summary>Context for module actions on a series detail dialog.</summary>
public sealed record TvSeriesActionContext(TvSeries Series);

/// <summary>Context for module actions on a movie detail dialog.</summary>
public sealed record TvMovieActionContext(TvMovie Movie);

/// <summary>Context for module actions on a remote/discovery media popup.</summary>
public sealed record TvMediaLinkContext(int TmdbId, bool IsMovie);

/// <summary>Context handed to modules that want to contribute content for a selected TV season.</summary>
public sealed record TvSeriesSeasonContext(TvSeries Series, TvSeason Season);

/// <summary>Context handed to modules that want to contribute an action for one episode tile.</summary>
public sealed record TvEpisodeActionContext(TvSeries? Series, TvEpisode Episode);
