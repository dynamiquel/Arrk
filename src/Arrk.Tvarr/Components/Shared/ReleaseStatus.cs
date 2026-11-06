using Arrk.Ui.Components;
using Arrk.Core.Models;

namespace Arrk.Tvarr.Components.Shared;

/// <summary>
/// Single source of truth for whether a movie or show has not been released on web/physical
/// media yet. Every surface that shows a "Not Released" marker, the media card status line,
/// the card badge and the media-popup badge, resolves it here, so they can never disagree.
/// </summary>
/// <remarks>
/// A movie is "released" once its digital (web) or physical window opens; a cinema-only run
/// (theatrical window open, no web/physical release) or an unresolved status still counts as
/// not released. A show is "released" once it has started airing.
/// </remarks>
public static class ReleaseStatus
{
    /// <summary>Library movies, using Radarr's status plus its release dates.</summary>
    public static bool MovieNotYetReleased(MovieStatus status, DateTime? inCinemas, DateTime? digital, DateTime? physical)
    {
        if (FormatHelper.IsReleased(digital) || FormatHelper.IsReleased(physical))
        {
            return false; // already available on web or physical media
        }

        if (FormatHelper.IsReleased(inCinemas))
        {
            return true; // cinema-only
        }

        return status is MovieStatus.Announced or MovieStatus.InCinemas or MovieStatus.Tba;
    }

    /// <summary>Discovery movies, using TMDB's status plus its release dates.</summary>
    public static bool MovieNotYetReleased(string? tmdbStatus, DateTime? inCinemas, DateTime? digital, DateTime? physical) =>
        MovieNotYetReleased(ToMovieStatus(tmdbStatus, inCinemas, digital, physical), inCinemas, digital, physical);

    /// <summary>Library shows, using Sonarr's status plus the first-air date.</summary>
    public static bool SeriesNotYetReleased(SeriesStatus status, DateTime? firstAired) =>
        !FormatHelper.IsReleased(firstAired) && (firstAired.HasValue || status == SeriesStatus.Upcoming);

    /// <summary>Discovery shows, using TMDB's status plus the first-air date.</summary>
    public static bool SeriesNotYetReleased(string? tmdbStatus, DateTime? firstAirDate) =>
        !FormatHelper.IsReleased(firstAirDate)
        && (firstAirDate.HasValue || tmdbStatus is "Planned" or "In Production");

    /// <summary>The release-window label shown in the media popup's Status field (library movies).</summary>
    public static string MovieReleaseLabel(MovieStatus status, DateTime? inCinemas, DateTime? digital, DateTime? physical) =>
        status switch
        {
            MovieStatus.InCinemas => "In Cinema",
            MovieStatus.Announced => "Announced",
            MovieStatus.Tba => "TBA",
            MovieStatus.Deleted => "Deleted",
            _ => FormatHelper.MovieReleaseStage(inCinemas, digital, physical) ?? "Released"
        };

    /// <summary>The release-window label shown in the media popup's Status field (discovery movies).</summary>
    public static string MovieReleaseLabel(string? tmdbStatus, DateTime? inCinemas, DateTime? digital, DateTime? physical) =>
        string.Equals(tmdbStatus, "Released", StringComparison.OrdinalIgnoreCase)
            ? FormatHelper.MovieReleaseStage(inCinemas, digital, physical) ?? "Released"
            : tmdbStatus ?? "";

    /// <summary>Normalises TMDB's free-text movie status onto the shared <see cref="MovieStatus"/> axis.</summary>
    static MovieStatus ToMovieStatus(string? tmdbStatus, DateTime? inCinemas, DateTime? digital, DateTime? physical)
    {
        if (FormatHelper.IsReleased(digital) || FormatHelper.IsReleased(physical))
        {
            return MovieStatus.Released;
        }

        if (FormatHelper.IsReleased(inCinemas))
        {
            return MovieStatus.InCinemas;
        }

        return tmdbStatus?.Trim().ToLowerInvariant() switch
        {
            "post production" or "in production" or "planned" or "rumored" or "canceled" => MovieStatus.Announced,
            _ => MovieStatus.Released
        };
    }
}
