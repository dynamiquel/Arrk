using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Docker;

namespace Arrk.Bazarr;

/// <summary>
/// Reads Bazarr's generated API key from <c>/config/config/config.yaml</c> inside its container.
/// The key cannot be supplied through Bazarr's environment, and writing it before first start would
/// fight Bazarr's own first-run generation, so Arrk reads the key it created and caches it.
/// </summary>
public sealed class BazarrApiKeyStore(
    ContainerManager containers,
    ArrkInstance arrk,
    ILogger<BazarrApiKeyStore> logger)
{
    static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(15);

    readonly SemaphoreSlim _lock = new(1, 1);
    string? _cached;
    DateTime _nextRead = DateTime.MinValue;

    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null && DateTime.UtcNow < _nextRead)
            return _cached;

        await _lock.WaitAsync(ct);
        try
        {
            if (_cached is not null && DateTime.UtcNow < _nextRead)
                return _cached;

            string containerName = arrk.GetContainerName("bazarr");
            Arrk.Core.Models.ContainerInfo? container = await containers.GetContainer(containerName, ct);
            if (container is null)
            {
                _nextRead = DateTime.UtcNow + RetryAfterFailure;
                return _cached;
            }

            string yaml = await containers.ExecInContainer(
                container.Id,
                ["cat", "/config/config/config.yaml"],
                ct);

            string? key = ParseApiKey(yaml);
            if (string.IsNullOrWhiteSpace(key))
            {
                _nextRead = DateTime.UtcNow + RetryAfterFailure;
                return _cached;
            }

            _cached = key;
            _nextRead = DateTime.UtcNow + CacheFor;
            return _cached;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read the Bazarr API key from its container");
            _nextRead = DateTime.UtcNow + RetryAfterFailure;
            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Extracts <c>auth.apikey</c> without needing a YAML parser in the hot path.</summary>
    public static string? ParseApiKey(string yaml)
    {
        bool inAuth = false;
        foreach (string rawLine in yaml.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            string trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;

            if (indent == 0)
            {
                inAuth = trimmed.Equals("auth:", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inAuth)
                continue;

            if (trimmed.StartsWith("apikey:", StringComparison.OrdinalIgnoreCase))
            {
                string value = trimmed["apikey:".Length..].Trim().Trim('\'', '"');
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }
}
