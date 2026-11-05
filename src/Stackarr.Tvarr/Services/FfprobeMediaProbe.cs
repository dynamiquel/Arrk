using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Stackarr.Core.Models;

namespace Stackarr.Tvarr.Services;

/// <summary>
/// <see cref="IMediaProbe"/> backed by <c>ffprobe</c>. ffprobe is preferred over the arrs' own
/// media info because it exposes the stream-level detail (container, profile, level, colour,
/// sample rate, subtitle titles) that Jellyfin-style media info shows.
/// </summary>
public sealed class FfprobeMediaProbe : IMediaProbe
{
    readonly string _ffprobePath;
    readonly ILogger<FfprobeMediaProbe>? _logger;
    readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    sealed record CacheEntry(long Size, DateTime ModifiedUtc, MediaTechnicalInfo Info);

    public FfprobeMediaProbe(ILogger<FfprobeMediaProbe>? logger = null, string ffprobePath = "ffprobe")
    {
        _logger = logger;
        _ffprobePath = ffprobePath;
    }

    public async Task<MediaTechnicalInfo?> ProbeAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
                return null;
        }
        catch
        {
            return null;
        }

        if (_cache.TryGetValue(file.FullName, out CacheEntry? cached) &&
            cached.Size == file.Length && cached.ModifiedUtc == file.LastWriteTimeUtc)
        {
            return cached.Info;
        }

        try
        {
            ProcessStartInfo startInfo = new(_ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("quiet");
            startInfo.ArgumentList.Add("-print_format");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add("-show_format");
            startInfo.ArgumentList.Add("-show_streams");
            startInfo.ArgumentList.Add(path);

            using Process process = new() { StartInfo = startInfo };
            process.Start();

            string json = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return null;

            MediaTechnicalInfo info = FfprobeParser.Parse(json, path, file.Length);

            if (_cache.Count > 128)
                _cache.Clear();
            _cache[file.FullName] = new CacheEntry(file.Length, file.LastWriteTimeUtc, info);

            return info;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // ffprobe missing, unreadable file, malformed output, fall back to arr metadata.
            _logger?.LogDebug(ex, "ffprobe scan failed for {Path}", path);
            return null;
        }
    }
}
