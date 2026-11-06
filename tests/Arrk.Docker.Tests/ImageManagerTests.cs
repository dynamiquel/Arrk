using Docker.DotNet;
using Arrk.TestSupport;

namespace Arrk.Docker.Tests;

/// <summary>
/// Image pull/existence/digest behaviour against a real daemon, plus the pure
/// age/outdated helpers.
/// </summary>
[NotInParallel("docker")]
[RequiresDocker]
public class ImageManagerTests
{
    [Test]
    public async Task PullImage_MakesImageAvailable()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ImageManager imageManager = new(client);

        await imageManager.PullImage(DockerTestHelper.TestImage);

        await Assert.That(await imageManager.ImageExists(DockerTestHelper.TestImage)).IsTrue();
    }

    [Test]
    public async Task PullImage_AlreadyPresent_DoesNotFail()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ImageManager imageManager = new(client);

        await imageManager.PullImage(DockerTestHelper.TestImage);
        await imageManager.PullImage(DockerTestHelper.TestImage);

        await Assert.That(await imageManager.ImageExists(DockerTestHelper.TestImage)).IsTrue();
    }

    [Test]
    public async Task ImageExists_ReturnsFalseForUnknownImage()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ImageManager imageManager = new(client);

        await Assert.That(await imageManager.ImageExists("nonexistent-image-does-not-exist:latest")).IsFalse();
    }

    [Test]
    public async Task ResolveDigestAsync_ReturnsSha256Digest()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ImageManager imageManager = new(client);

        string? digest = await imageManager.ResolveDigestAsync(DockerTestHelper.TestImage);

        await Assert.That(digest).IsNotNull();
        await Assert.That(digest!).StartsWith("sha256:");
    }

    [Test]
    public async Task GetLocalImageDigestAsync_UnknownImage_ReturnsNull()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ImageManager imageManager = new(client);

        string? digest = await imageManager.GetLocalImageDigestAsync("nonexistent-image-does-not-exist:latest");

        await Assert.That(digest).IsNull();
    }

    [Test]
    public async Task IsImageBeingRepulled_TracksNamesAndTags()
    {
        using DockerClient client = DockerTestHelper.CreateClient();
        ImageManager imageManager = new(client);
        imageManager.ImagesBeingRepulled["linuxserver/radarr:latest"] = 0;

        await Assert.That(imageManager.IsImageBeingRepulled("linuxserver/radarr:latest")).IsTrue();
        await Assert.That(imageManager.IsImageBeingRepulled("linuxserver/radarr:5.0")).IsTrue();
        await Assert.That(imageManager.IsImageBeingRepulled("linuxserver/sonarr:latest")).IsFalse();
    }

    [Test]
    public async Task GetImageAge_ReturnsElapsedTime()
    {
        DateTime created = DateTime.UtcNow.AddHours(-2);

        TimeSpan age = ImageManager.GetImageAge(created);

        await Assert.That(age.TotalHours).IsGreaterThanOrEqualTo(1.9);
    }

    [Test]
    public async Task GetAgeDescription_FormatsBasedOnMagnitude()
    {
        await Assert.That(ImageManager.GetAgeDescription(TimeSpan.FromHours(5))).IsEqualTo("5h ago");
        await Assert.That(ImageManager.GetAgeDescription(TimeSpan.FromDays(3))).IsEqualTo("3d ago");
        await Assert.That(ImageManager.GetAgeDescription(TimeSpan.FromDays(60))).IsEqualTo("2mo ago");
        await Assert.That(ImageManager.GetAgeDescription(TimeSpan.FromDays(400))).IsEqualTo("1y ago");
    }

    [Test]
    public async Task IsImageOutdated_TrueBeyondThirtyDays()
    {
        await Assert.That(ImageManager.IsImageOutdated(TimeSpan.FromDays(31))).IsTrue();
        await Assert.That(ImageManager.IsImageOutdated(TimeSpan.FromDays(30))).IsFalse();
        await Assert.That(ImageManager.IsImageOutdated(TimeSpan.FromDays(10))).IsFalse();
    }
}
