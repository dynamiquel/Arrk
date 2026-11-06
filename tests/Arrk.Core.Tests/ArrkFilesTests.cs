using Arrk.Core.Models;

namespace Arrk.Core.Tests;

public class ArrkFilesTests
{
    [Test]
    [Arguments("arrk.yaml", "stackarr.yaml")]
    [Arguments("arrk.json", "stackarr.json")]
    [Arguments("arrk.db", "stackarr.db")]
    public async Task ExistingStackarrFiles_AreReusedWithoutMovingOrOverwritingThem(string currentName, string legacyName)
    {
        string root = Path.Combine(Path.GetTempPath(), $"stackarr-upgrade-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "config");
        Directory.CreateDirectory(directory);
        Func<string, string> resolve = currentName switch
        {
            "arrk.yaml" => ArrkFiles.ConfigPath,
            "arrk.json" => ArrkFiles.ConfigJsonPath,
            _ => ArrkFiles.DatabasePath
        };

        try
        {
            string currentPath = Path.Combine(directory, currentName);
            string legacyPath = Path.Combine(directory, legacyName);
            await Assert.That(resolve(root)).IsEqualTo(currentPath);

            await File.WriteAllTextAsync(legacyPath, "existing server data");
            await Assert.That(resolve(root)).IsEqualTo(legacyPath);
            await Assert.That(File.Exists(currentPath)).IsFalse();

            // Even an accidentally created new file must not hide the existing server's data.
            await File.WriteAllTextAsync(currentPath, "new empty installation");
            await Assert.That(resolve(root)).IsEqualTo(legacyPath);
            await Assert.That(await File.ReadAllTextAsync(legacyPath)).IsEqualTo("existing server data");

            File.Delete(legacyPath);
            await Assert.That(resolve(root)).IsEqualTo(currentPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
