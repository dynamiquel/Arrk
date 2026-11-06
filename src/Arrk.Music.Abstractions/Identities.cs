namespace Arrk.Music.Abstractions;

/// <summary>The Deezer identity of a music item, when it has one.</summary>
public interface IDeezerIdentity
{
    long DeezerId { get; }
}

/// <summary>Provider-id keys used by music requests, watchlists and release searches.</summary>
public static class MusicProviderIds
{
    /// <summary>Deezer album id.</summary>
    public const string Deezer = "deezer";

    /// <summary>Deezer artist id.</summary>
    public const string DeezerArtist = "deezerartist";
}
