using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.Core.Configuration;
using Stackarr.TestSupport;
using Stackarr.Tvarr.Models;
using Stackarr.Tvarr.Services;

namespace Stackarr.Tvarr.Tests;

public class TvUploadStoreTests
{
    static (TvUploadStore Store, string Root, TestDbContextFactory Db) Build(string? downloads = null)
    {
        string root = downloads ?? Path.Combine(Path.GetTempPath(), $"stackarr-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        StackarrConfig config = new()
        {
            Storage = new StorageConfig { Downloads = root }
        };
        TvConfig tv = new()
        {
            Uploads = new TvUploadConfig { MaxFileSizeGb = 1, RetentionHours = 1 }
        };

        TestDbContextFactory db = new(TestDb.Options());
        TvUploadStore store = new(
            db,
            new MutableOptionsMonitor<StackarrConfig>(config),
            new MutableOptionsMonitor<TvConfig>(tv),
            NullLogger<TvUploadStore>.Instance);

        return (store, root, db);
    }

    static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    [Test]
    public async Task Stage_WritesUnderDownloadsAndRecordsTheContainerPath()
    {
        (TvUploadStore store, string root, _) = Build();
        string batch = Guid.NewGuid().ToString("N");

        TvUploadRecord record = await store.StageAsync(
            Content("media"), "Movie.2026.1080p.mkv", 5, batch, "movie", "movie");

        await Assert.That(record.Status).IsEqualTo(TvUploadStatus.Staged);
        await Assert.That(record.ContainerPath).IsEqualTo($"/downloads/stackarr-uploads/{batch}/Movie.2026.1080p.mkv");
        await Assert.That(File.Exists(record.StoredPath)).IsTrue();
        await Assert.That(record.StoredPath.StartsWith(root, StringComparison.Ordinal)).IsTrue();
        await Assert.That(record.SizeBytes).IsEqualTo(5);
    }

    [Test]
    public async Task Stage_RejectsUnsupportedExtensionsBeforeWriting()
    {
        (TvUploadStore store, string root, _) = Build();
        string batch = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<TvUploadException>(async () =>
            await store.StageAsync(Content("not media"), "notes.txt", 9, batch, "auto", ""));

        await Assert.That(Directory.Exists(Path.Combine(root, TvUploadStore.StagingFolderName, batch))).IsFalse();
    }

    [Test]
    public async Task Stage_RejectsFilesOverTheConfiguredLimit()
    {
        (TvUploadStore store, _, _) = Build();
        string batch = Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<TvUploadException>(async () =>
            await store.StageAsync(
                Content("small"),
                "Movie.mkv",
                (2L * 1024 * 1024 * 1024),
                batch,
                "movie",
                "movie"));
    }

    [Test]
    public async Task Stage_SanitisesTraversalAndKeepsTheExtension()
    {
        (TvUploadStore store, _, _) = Build();
        string batch = Guid.NewGuid().ToString("N");

        TvUploadRecord record = await store.StageAsync(
            Content("media"), "../../evil.MKV", 5, batch, "movie", "movie");

        await Assert.That(record.FileName).EndsWith(".MKV");
        await Assert.That(record.FileName.Contains("..", StringComparison.Ordinal)).IsFalse();
        await Assert.That(File.Exists(record.StoredPath)).IsTrue();
    }

    [Test]
    public async Task CancelBatch_RemovesFilesAndMarksRows()
    {
        (TvUploadStore store, _, _) = Build();
        string batch = Guid.NewGuid().ToString("N");
        TvUploadRecord record = await store.StageAsync(
            Content("media"), "Movie.mkv", 5, batch, "movie", "movie");

        await store.CancelBatchAsync(batch);

        List<TvUploadRecord> rows = await store.GetBatchAsync(batch);
        await Assert.That(rows.Single().Status).IsEqualTo(TvUploadStatus.Removed);
        await Assert.That(File.Exists(record.StoredPath)).IsFalse();
    }

    [Test]
    public async Task GetBatch_RejectsNonGuidBatchIds()
    {
        (TvUploadStore store, _, _) = Build();

        await Assert.ThrowsAsync<TvUploadException>(async () => await store.GetBatchAsync("../etc"));
    }

    [Test]
    public async Task SupportedFormats_AcceptCommonVideoAndSubtitleTypes()
    {
        await Assert.That(TvUploadFormats.IsVideo("movie.mkv")).IsTrue();
        await Assert.That(TvUploadFormats.IsVideo("episode.MP4")).IsTrue();
        await Assert.That(TvUploadFormats.IsSubtitle("movie.en.srt")).IsTrue();
        await Assert.That(TvUploadFormats.IsSupported("movie.exe")).IsFalse();
        await Assert.That(TvUploadFormats.Accept).Contains("mkv");
        await Assert.That(TvUploadFormats.Accept).Contains("srt");
    }
}
