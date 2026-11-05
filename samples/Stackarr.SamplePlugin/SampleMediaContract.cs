using Stackarr.Tv.Abstractions;
using Stackarr.Media.Abstractions;

namespace Stackarr.SamplePlugin;

/// <summary>
/// A compile-checked example of the capability contract style: a plugin implements only the
/// media capabilities it actually has, and consumers pattern-match for them.
/// </summary>
public sealed class SampleMovie :
    IMediaDescriptor,
    IMediaArtwork,
    IMediaLibraryState,
    ITmdbIdentity
{
    public string Title => "Sample movie";

    public string OriginalTitle => Title;

    public int Year => 2026;

    public DateTime? ReleaseDate => new(2026, 1, 1);

    public string Overview => "A plugin-provided media item.";

    public IReadOnlyList<string> Genres => ["Documentary"];

    public double? Rating => 8.0;

    public int? RuntimeMinutes => 90;

    public string Certification => "PG";

    public string OriginalLanguage => "en";

    public string PosterUrl => "";

    public string FanartUrl => "";

    public string BannerUrl => "";

    public string LogoUrl => "";

    public bool Monitored => false;

    public bool HasFile => false;

    public long SizeOnDisk => 0;

    public DateTime AddedAt => DateTime.UnixEpoch;

    public QualityInfo? Quality => null;

    public MediaFileInfo? File => null;

    public int TmdbId => 0;
}
