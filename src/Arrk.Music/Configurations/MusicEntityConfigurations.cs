using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Arrk.Core;
using Arrk.Music.Models;

namespace Arrk.Music.Configurations;

public class MusicArtistConfiguration : ModuleEntityConfiguration<MusicArtist>
{
    public override void Configure(EntityTypeBuilder<MusicArtist> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(512);
        builder.Property(x => x.SortName).HasMaxLength(512);
        builder.Property(x => x.Overview).HasMaxLength(8192);
        builder.Property(x => x.GenresJson).HasMaxLength(2048);
        builder.Property(x => x.PosterUrl).HasMaxLength(1024);
        builder.Property(x => x.FanartUrl).HasMaxLength(1024);
        builder.HasIndex(x => x.DeezerId);
        builder.HasIndex(x => x.Name);
    }
}

public class MusicAlbumConfiguration : ModuleEntityConfiguration<MusicAlbum>
{
    public override void Configure(EntityTypeBuilder<MusicAlbum> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(512);
        builder.Property(x => x.AlbumType).HasMaxLength(64);
        builder.Property(x => x.Overview).HasMaxLength(8192);
        builder.Property(x => x.PosterUrl).HasMaxLength(1024);
        builder.HasIndex(x => x.ArtistId);
        builder.HasIndex(x => x.DeezerId);
        builder.HasOne(x => x.Artist)
            .WithMany(a => a.Albums)
            .HasForeignKey(x => x.ArtistId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class MusicTrackConfiguration : ModuleEntityConfiguration<MusicTrack>
{
    public override void Configure(EntityTypeBuilder<MusicTrack> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Title).HasMaxLength(1024);
        builder.Property(x => x.FilePath).HasMaxLength(1024);
        builder.HasIndex(x => x.AlbumId);
        builder.HasIndex(x => x.ArtistId);
        builder.HasIndex(x => x.DeezerId);
        // The lyrics sweep selects by state, so it must be cheap.
        builder.HasIndex(x => x.LyricsState);
        builder.HasOne<MusicAlbum>()
            .WithMany(a => a.Tracks)
            .HasForeignKey(x => x.AlbumId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class LibraryIntentConfiguration : ModuleEntityConfiguration<LibraryIntent>
{
    public override void Configure(EntityTypeBuilder<LibraryIntent> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Source).HasMaxLength(32);
        builder.Property(x => x.Title).HasMaxLength(512);
        builder.Property(x => x.Artist).HasMaxLength(512);
        builder.HasIndex(x => new { x.Kind, x.TargetId });
    }
}

public class GrabRecordConfiguration : ModuleEntityConfiguration<GrabRecord>
{
    public override void Configure(EntityTypeBuilder<GrabRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Source).HasMaxLength(32);
        builder.Property(x => x.Identifier).HasMaxLength(256);
        builder.Property(x => x.ReleaseTitle).HasMaxLength(1024);
        builder.Property(x => x.State).HasMaxLength(32);
        builder.HasIndex(x => x.AlbumId);
        builder.HasIndex(x => x.DeezerTrackId);
        builder.HasIndex(x => x.DeezerAlbumId);
    }
}

public class MusicAcquireQueueConfiguration : ModuleEntityConfiguration<MusicAcquireQueueItem>
{
    public override void Configure(EntityTypeBuilder<MusicAcquireQueueItem> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(512);
        builder.Property(x => x.Artist).HasMaxLength(512);
        builder.Property(x => x.Title).HasMaxLength(1024);
        builder.Property(x => x.LastError).HasMaxLength(1024);
        builder.HasIndex(x => x.Key).IsUnique();
    }
}
