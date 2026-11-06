using Arrk.Core.Models;
using Arrk.Music.Services;

namespace Arrk.Music.Tests;

/// <summary>
/// Album and track names ultimately come from remote metadata. They must never become a parent
/// directory reference that escapes the library root.
/// </summary>
public class MusicLibraryPathTests
{
    [Test]
    [Arguments("..", "..")]
    [Arguments(".", ".")]
    [Arguments("...", "...")]
    [Arguments(".. / ..", ".. / ..")]
    public async Task AlbumFolderName_DoesNotEscapeTheLibraryRoot(string artist, string title)
    {
        Album album = new()
        {
            ArtistName = artist,
            Title = title
        };

        string folder = MusicAcquisitionService.AlbumFolderName(album);

        await Assert.That(folder).DoesNotContain("..");
        await Assert.That(folder).DoesNotStartWith("/");
        await Assert.That(Path.IsPathRooted(folder)).IsFalse();
    }

    [Test]
    public async Task AlbumFolderName_KeepsOrdinaryNames()
    {
        Album album = new() { ArtistName = "Martin O'Donnell", Title = "Halo 3" };

        await Assert.That(MusicAcquisitionService.AlbumFolderName(album))
            .IsEqualTo(Path.Combine("Martin O'Donnell", "Halo 3"));
    }
}
