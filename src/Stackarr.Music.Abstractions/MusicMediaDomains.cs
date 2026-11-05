using Stackarr.Media.Abstractions;

namespace Stackarr.Music.Abstractions;

/// <summary>Media-domain keys owned by the music feature.</summary>
public static class MusicMediaDomains
{
    public const string Music = "music";

    public static MediaDomain MusicDomain => new(Music);
}

/// <summary>Release-search kinds owned by the music feature.</summary>
public static class MusicReleaseKinds
{
    public const string Album = "album";
    public const string Track = "track";
}
