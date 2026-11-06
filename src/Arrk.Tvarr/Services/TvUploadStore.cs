using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Services;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Services;

/// <summary>A user-facing upload failure that the dialog can show verbatim.</summary>
public sealed class TvUploadException(string message) : Exception(message);

/// <summary>
/// Owns the staging area and the durable upload rows. Files are streamed straight from the HTTP
/// request into <c>&lt;downloads&gt;/stackarr-uploads/&lt;batch&gt;/</c>, which both arr containers
/// already mount at <c>/downloads</c>, so no extra bind mount is needed. The arr imports with
/// <c>copy</c> mode because its downloads mount is read-only; Arrk removes the staged copy
/// afterwards.
/// </summary>
public sealed class TvUploadStore(
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    IOptionsMonitor<ArrkConfig> config,
    IOptionsMonitor<TvConfig> tvConfig,
    ILogger<TvUploadStore> logger)
{
    public const string StagingFolderName = "stackarr-uploads";
    public const string ContainerDownloadsRoot = "/downloads";

    public long MaxFileSizeBytes =>
        Math.Max(1, tvConfig.CurrentValue.Uploads.MaxFileSizeGb) * 1024L * 1024L * 1024L;

    public static bool IsValidBatchId(string batchId) =>
        Guid.TryParse(batchId, out _);

    public string GetLocalFolder(string batchId)
    {
        EnsureBatchId(batchId);
        VolumePathResolver resolver = new(config.CurrentValue.Storage);
        string downloads = resolver.ResolveToLocalPath(RelativeVolumes.Downloads);
        return Path.Combine(downloads, StagingFolderName, batchId);
    }

    public string GetContainerFolder(string batchId)
    {
        EnsureBatchId(batchId);
        return $"{ContainerDownloadsRoot}/{StagingFolderName}/{batchId}";
    }

    public async Task<TvUploadRecord> StageAsync(
        Stream content,
        string fileName,
        long? contentLength,
        string batchId,
        string kind,
        string mediaType,
        CancellationToken ct = default)
    {
        EnsureBatchId(batchId);

        if (!TvUploadFormats.IsSupported(fileName))
        {
            throw new TvUploadException(
                $"\"{fileName}\" is not a supported media file. Upload one of: {TvUploadFormats.SupportedLabel}.");
        }

        if (contentLength is long length && length > MaxFileSizeBytes)
        {
            throw new TvUploadException(
                $"\"{fileName}\" is larger than the {tvConfig.CurrentValue.Uploads.MaxFileSizeGb} GB upload limit.");
        }

        string folder = GetLocalFolder(batchId);
        Directory.CreateDirectory(folder);

        string safeName = TvUploadFormats.SafeFileName(fileName);
        string finalPath = UniquePath(folder, safeName);
        string tempPath = finalPath + ".part";

        long written = 0;
        try
        {
            await using (FileStream file = new(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1024 * 1024,
                useAsync: true))
            {
                byte[] buffer = new byte[1024 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    written += read;
                    if (written > MaxFileSizeBytes)
                    {
                        throw new TvUploadException(
                            $"\"{fileName}\" is larger than the {tvConfig.CurrentValue.Uploads.MaxFileSizeGb} GB upload limit.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            File.Move(tempPath, finalPath);
        }
        catch
        {
            TryDelete(tempPath);
            TryDelete(finalPath);
            throw;
        }

        TvUploadRecord record = new()
        {
            BatchId = batchId,
            FileName = Path.GetFileName(finalPath),
            StoredPath = finalPath,
            ContainerPath = $"{GetContainerFolder(batchId)}/{Path.GetFileName(finalPath)}",
            SizeBytes = written,
            Status = TvUploadStatus.Staged,
            Kind = string.IsNullOrWhiteSpace(kind) ? "auto" : kind,
            MediaType = mediaType ?? "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        db.Set<TvUploadRecord>().Add(record);
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Staged local upload {File} ({Size} bytes) in batch {Batch}",
            record.FileName, record.SizeBytes, batchId);

        return record;
    }

    public async Task<List<TvUploadRecord>> GetBatchAsync(string batchId, CancellationToken ct = default)
    {
        EnsureBatchId(batchId);
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<TvUploadRecord>()
            .AsNoTracking()
            .Where(record => record.BatchId == batchId)
            .OrderBy(record => record.Id)
            .ToListAsync(ct);
    }

    public async Task UpdateAsync(TvUploadRecord record, CancellationToken ct = default)
    {
        record.UpdatedAt = DateTime.UtcNow;
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        db.Set<TvUploadRecord>().Update(record);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes the staged files and the rows for a batch the user cancelled.</summary>
    public async Task CancelBatchAsync(string batchId, CancellationToken ct = default)
    {
        List<TvUploadRecord> records = await GetBatchAsync(batchId, ct);
        foreach (TvUploadRecord record in records)
        {
            TryDelete(record.StoredPath);
            record.Status = TvUploadStatus.Removed;
            record.UpdatedAt = DateTime.UtcNow;
        }

        if (records.Count > 0)
        {
            await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            db.Set<TvUploadRecord>().UpdateRange(records);
            await db.SaveChangesAsync(ct);
        }

        TryDeleteFolder(GetLocalFolder(batchId));
    }

    /// <summary>Removes staging folders and rows older than the configured retention window.</summary>
    public async Task CleanupExpiredAsync(CancellationToken ct = default)
    {
        int retentionHours = Math.Max(1, tvConfig.CurrentValue.Uploads.RetentionHours);
        DateTime cutoff = DateTime.UtcNow.AddHours(-retentionHours);

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        List<TvUploadRecord> expired = await db.Set<TvUploadRecord>()
            .Where(record => record.UpdatedAt < cutoff && record.Status != TvUploadStatus.Imported)
            .OrderBy(record => record.Id)
            .Take(500)
            .ToListAsync(ct);

        foreach (TvUploadRecord record in expired)
        {
            TryDelete(record.StoredPath);
            record.Status = TvUploadStatus.Removed;
            record.UpdatedAt = DateTime.UtcNow;
        }

        if (expired.Count > 0)
        {
            db.Set<TvUploadRecord>().UpdateRange(expired);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Removed {Count} expired local-upload files", expired.Count);
        }

        string stagingRoot = Path.Combine(
            new VolumePathResolver(config.CurrentValue.Storage).ResolveToLocalPath(RelativeVolumes.Downloads),
            StagingFolderName);

        if (!Directory.Exists(stagingRoot))
            return;

        foreach (string folder in Directory.EnumerateDirectories(stagingRoot))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(folder) < cutoff
                    && !await db.Set<TvUploadRecord>().AnyAsync(
                        record => record.BatchId == Path.GetFileName(folder)
                            && record.Status != TvUploadStatus.Removed
                            && record.Status != TvUploadStatus.Imported,
                        ct))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not remove old upload staging folder {Folder}", folder);
            }
        }
    }

    /// <summary>Removes the staged copies after a successful arr import.</summary>
    public void RemoveImportedFiles(IEnumerable<TvUploadRecord> records)
    {
        List<TvUploadRecord> list = [.. records];
        foreach (TvUploadRecord record in list)
            TryDelete(record.StoredPath);

        foreach (IGrouping<string, TvUploadRecord> batch in list.GroupBy(record => record.BatchId))
        {
            string folder = GetLocalFolder(batch.Key);
            try
            {
                if (Directory.Exists(folder) && !Directory.EnumerateFiles(folder).Any())
                    Directory.Delete(folder);
            }
            catch
            {
                // A later sweep can remove an empty folder.
            }
        }
    }

    public void RemoveFile(TvUploadRecord record) => TryDelete(record.StoredPath);

    static void EnsureBatchId(string batchId)
    {
        if (!IsValidBatchId(batchId))
            throw new TvUploadException("The upload batch identifier is invalid.");
    }

    static string UniquePath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 2; index < 1000; index++)
        {
            candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: the retention sweep will try again.
        }
    }

    static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort: the retention sweep will try again.
        }
    }
}
