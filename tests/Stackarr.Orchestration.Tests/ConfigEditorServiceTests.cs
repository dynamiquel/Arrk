using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Configuration;
using Stackarr.Core.Configuration;
using Stackarr.Core.Models;

namespace Stackarr.Orchestration.Tests;

/// <summary>
/// Behaviour of <see cref="ConfigEditorService"/> (save/undo/version history).
/// </summary>
[NotInParallel("config-editor")]
public class ConfigEditorServiceTests : IDisposable
{
    readonly string _tempDir;
    readonly ConfigEditorService _service;

    public ConfigEditorServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"stackarr-config-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        ConfigSectionRegistry registry = new ConfigSectionRegistry();
        ConfigDefaults.RegisterCoreSections(registry);

        _service = new(_tempDir, registry, new NullServiceProvider(), NullLogger<ConfigEditorService>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    async Task CleanState()
    {
        if (Directory.Exists(_service.VersionsDirectory))
        {
            foreach (string f in Directory.GetFiles(_service.VersionsDirectory))
                File.Delete(f);
        }
        if (File.Exists(_service.ConfigFilePath)) File.Delete(_service.ConfigFilePath);
        if (File.Exists(_service.ConfigFilePath + ".bak")) File.Delete(_service.ConfigFilePath + ".bak");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ConfigFilePath_UsesInjectedDirectory()
    {
        // The injected directory must be honoured, so the file lives under the temp dir
        // rather than any absolute /config path.
        await Assert.That(_service.ConfigFilePath)
            .IsEqualTo(Path.Combine(_tempDir, StackarrFiles.ConfigFileName));
        await Assert.That(_service.ConfigFilePath).StartsWith(_tempDir);
    }

    [Test]
    public async Task ReadConfig_NoFile_ReturnsEmpty()
    {
        await CleanState();

        string result = await _service.ReadConfigAsync();

        await Assert.That(result).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ReadConfig_WithFile_ReturnsContent()
    {
        await CleanState();
        await File.WriteAllTextAsync(_service.ConfigFilePath, "storage:\n  media: /mnt/media");

        string result = await _service.ReadConfigAsync();

        await Assert.That(result).IsEqualTo("storage:\n  media: /mnt/media");
    }

    [Test]
    public async Task SaveConfig_CreatesFile()
    {
        await CleanState();

        ConfigSaveResult result = await _service.SaveConfigAsync("storage:\n  media: /test");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(File.Exists(_service.ConfigFilePath)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(_service.ConfigFilePath)).IsEqualTo("storage:\n  media: /test");
    }

    [Test]
    public async Task SaveConfig_WithExistingFile_CreatesBackupVersionAndBakFile()
    {
        await CleanState();
        await File.WriteAllTextAsync(_service.ConfigFilePath, "original: content");

        ConfigSaveResult result = await _service.SaveConfigAsync("new: content");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.BackedUpVersion).IsNotNull();

        List<ConfigVersion> versions = _service.GetVersions();
        await Assert.That(versions.Count).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(versions[0].FilePath)).IsEqualTo("original: content");

        string backupPath = _service.ConfigFilePath + ".bak";
        await Assert.That(File.Exists(backupPath)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(backupPath)).IsEqualTo("original: content");
    }

    [Test]
    public async Task SaveConfig_NoExistingFile_CreatesNoBackup()
    {
        await CleanState();

        ConfigSaveResult result = await _service.SaveConfigAsync("new: content");

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.BackedUpVersion).IsNull();
        await Assert.That(_service.GetVersions()).IsEmpty();
    }

    [Test]
    public async Task UndoAsync_RevertsToBackup()
    {
        await CleanState();
        await File.WriteAllTextAsync(_service.ConfigFilePath, "version1");
        await _service.SaveConfigAsync("version2");
        await _service.SaveConfigAsync("version3");

        string? result = await _service.UndoAsync();

        await Assert.That(result).IsNotNull();
        await Assert.That(await File.ReadAllTextAsync(_service.ConfigFilePath)).IsEqualTo("version2");
    }

    [Test]
    public async Task UndoAsync_NoBackup_ReturnsNull()
    {
        await CleanState();

        string? result = await _service.UndoAsync();

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task GetVersions_NoVersions_ReturnsEmpty()
    {
        await CleanState();

        await Assert.That(_service.GetVersions()).IsEmpty();
    }

    [Test]
    public async Task GetVersions_MultipleVersions_ReturnsSortedDescending()
    {
        await CleanState();
        await File.WriteAllTextAsync(_service.ConfigFilePath, "v0");
        await _service.SaveConfigAsync("v1");
        await Task.Delay(1050);
        await _service.SaveConfigAsync("v2");

        List<ConfigVersion> versions = _service.GetVersions();

        await Assert.That(versions.Count).IsEqualTo(2);
        await Assert.That(versions[0].Timestamp).IsGreaterThanOrEqualTo(versions[1].Timestamp);
    }

    [Test]
    public async Task RestoreVersionAsync_RestoresContent()
    {
        await CleanState();
        await File.WriteAllTextAsync(_service.ConfigFilePath, "current");
        await _service.SaveConfigAsync("v1");
        await _service.SaveConfigAsync("v2");

        List<ConfigVersion> versions = _service.GetVersions();
        string? result = await _service.RestoreVersionAsync(versions[0].FileName);

        await Assert.That(result).IsNotNull();
        await Assert.That(await File.ReadAllTextAsync(_service.ConfigFilePath)).IsEqualTo("v1");
    }

    [Test]
    public async Task RestoreVersionAsync_NonExistent_ReturnsNull()
    {
        await CleanState();

        string? result = await _service.RestoreVersionAsync("nonexistent.yaml");

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task RestoreVersionAsync_BeforeRestore_BacksUpCurrentContent()
    {
        await CleanState();
        await File.WriteAllTextAsync(_service.ConfigFilePath, "current");
        await _service.SaveConfigAsync("v1");

        List<ConfigVersion> versions = _service.GetVersions();
        await _service.RestoreVersionAsync(versions[0].FileName);

        string backupPath = _service.ConfigFilePath + ".bak";
        await Assert.That(File.Exists(backupPath)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(backupPath)).IsEqualTo("current");
    }

    [Test]
    public async Task MultipleSaves_CreateOneVersionPerPriorSave()
    {
        await CleanState();
        for (int i = 0; i < 5; i++)
        {
            await _service.SaveConfigAsync($"version {i}");
            await Task.Delay(1050);
        }

        await Assert.That(_service.GetVersions().Count).IsEqualTo(4);
    }

    [Test]
    public async Task VersionFiles_UseExpectedNamingPattern()
    {
        await CleanState();
        await File.WriteAllTextAsync(_service.ConfigFilePath, "existing");
        await _service.SaveConfigAsync("test");

        List<ConfigVersion> versions = _service.GetVersions();

        await Assert.That(versions.Count).IsEqualTo(1);
        await Assert.That(versions[0].FileName).StartsWith(StackarrFiles.VersionFileNamePrefix);
        await Assert.That(versions[0].FileName).EndsWith(".yaml");
    }
}

file class NullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
