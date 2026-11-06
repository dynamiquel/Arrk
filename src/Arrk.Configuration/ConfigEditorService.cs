using Microsoft.Extensions.Logging;
using Arrk.Core.Configuration;
using Arrk.Core.Models;

namespace Arrk.Configuration;

public class ConfigEditorService
{
    readonly string _configDirectory;
    readonly string _configFilePath;
    readonly string _versionsDirectory;
    readonly ILogger<ConfigEditorService> _logger;
    readonly ConfigSectionRegistry _sectionRegistry;

    public ConfigEditorService(
        string configDirectory,
        ConfigSectionRegistry sectionRegistry,
        IServiceProvider serviceProvider,
        ILogger<ConfigEditorService> logger)
    {
        _logger = logger;
        _configDirectory = configDirectory;
        _configFilePath = ArrkFiles.FilePath(_configDirectory, ArrkFiles.ConfigFileName);
        _versionsDirectory = Path.Combine(_configDirectory, "versions");
        _sectionRegistry = sectionRegistry;
    }

    public string ConfigFilePath => _configFilePath;
    public string VersionsDirectory => _versionsDirectory;

    public async Task<string> ReadConfigAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_configFilePath))
        {
            _logger.LogWarning("Config file not found at {Path}, returning empty", _configFilePath);
            return string.Empty;
        }

        return await File.ReadAllTextAsync(_configFilePath, ct);
    }

    public async Task<ConfigSaveResult> SaveConfigAsync(string content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_configDirectory);
        Directory.CreateDirectory(_versionsDirectory);

        string? backedUpVersion = (string?)null;

        if (File.Exists(_configFilePath))
        {
            backedUpVersion = await BackupCurrentVersion(ct);
        }

        await File.WriteAllTextAsync(_configFilePath, content, ct);

        _logger.LogInformation("Config saved to {Path}", _configFilePath);

        return new()
        {
            Success = true,
            BackedUpVersion = backedUpVersion
        };
    }

    public async Task<string?> UndoAsync(CancellationToken ct = default)
    {
        string backupPath = _configFilePath + ".bak";
        if (!File.Exists(backupPath))
        {
            _logger.LogWarning("No backup file found at {Path}", backupPath);
            return null;
        }

        string backupContent = await File.ReadAllTextAsync(backupPath, ct);

        Directory.CreateDirectory(_versionsDirectory);
        string versionFile = GetVersionFilePath();
        await File.WriteAllTextAsync(versionFile, backupContent, ct);

        await File.WriteAllTextAsync(_configFilePath, backupContent, ct);

        _logger.LogInformation("Undone to backup, saved previous version to {Version}", versionFile);

        return backupContent;
    }

    public List<ConfigVersion> GetVersions()
    {
        Directory.CreateDirectory(_versionsDirectory);

        List<ConfigVersion> versions = [];

        if (!Directory.Exists(_versionsDirectory))
            return versions;

        foreach (string file in Directory.GetFiles(_versionsDirectory, "*.yaml").OrderByDescending(f => f))
        {
            FileInfo fileInfo = new(file);
            versions.Add(new()
            {
                FileName = fileInfo.Name,
                FilePath = fileInfo.FullName,
                Timestamp = fileInfo.CreationTimeUtc,
                SizeBytes = fileInfo.Length
            });
        }

        return versions;
    }

    public async Task<string?> RestoreVersionAsync(string versionFileName, CancellationToken ct = default)
    {
        string versionPath = Path.Combine(_versionsDirectory, versionFileName);
        if (!File.Exists(versionPath))
        {
            _logger.LogWarning("Version file not found: {Path}", versionPath);
            return null;
        }

        string versionContent = await File.ReadAllTextAsync(versionPath, ct);

        Directory.CreateDirectory(_versionsDirectory);
        string backupFile = GetVersionFilePath();
        if (File.Exists(_configFilePath))
        {
            string currentContent = await File.ReadAllTextAsync(_configFilePath, ct);
            await File.WriteAllTextAsync(backupFile, currentContent, ct);
        }

        await File.WriteAllTextAsync(_configFilePath, versionContent, ct);

        _logger.LogInformation("Restored version {Version}", versionFileName);

        return versionContent;
    }

    async Task<string> BackupCurrentVersion(CancellationToken ct)
    {
        string currentContent = await File.ReadAllTextAsync(_configFilePath, ct);

        string backupPath = _configFilePath + ".bak";
        await File.WriteAllTextAsync(backupPath, currentContent, ct);

        string versionFile = GetVersionFilePath();
        await File.WriteAllTextAsync(versionFile, currentContent, ct);

        _logger.LogDebug("Backed up current config to {Backup} and {Version}", backupPath, versionFile);

        return versionFile;
    }

    string GetVersionFilePath()
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        return Path.Combine(_versionsDirectory, $"{ArrkFiles.VersionFileNamePrefix}{timestamp}.yaml");
    }
}

public class ConfigSaveResult
{
    public bool Success { get; set; }
    public string? BackedUpVersion { get; set; }
}

public class ConfigVersion
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public long SizeBytes { get; set; }
}
