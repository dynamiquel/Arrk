using Arrk.Jellyfin;

namespace Arrk.Jellyfin.Tests;

public class JellyfinConfigTests
{
    [Test]
    public async Task LibraryDefaults_AreIndependentPerCall()
    {
        List<JellyfinLibraryConfig> first = JellyfinLibraryConfig.Defaults();
        List<JellyfinLibraryConfig> second = JellyfinLibraryConfig.Defaults();

        first[0].Name = "Changed";

        await Assert.That(second[0].Name).IsEqualTo("Movies");
    }
}
