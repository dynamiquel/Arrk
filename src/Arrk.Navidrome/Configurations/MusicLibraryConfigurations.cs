using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Arrk.Core;
using Arrk.Navidrome.Models;

namespace Arrk.Navidrome.Configurations;

public class MusicPlaylistOwnerConfiguration : ModuleEntityConfiguration<MusicPlaylistOwner>
{
    public override void Configure(EntityTypeBuilder<MusicPlaylistOwner> builder)
    {
        builder.HasKey(x => x.PlaylistId);
        builder.Property(x => x.PlaylistId).HasMaxLength(128);
        builder.HasIndex(x => x.OwnerUserId);
    }
}

public class MusicPlayHistoryConfiguration : ModuleEntityConfiguration<MusicPlayHistory>
{
    public override void Configure(EntityTypeBuilder<MusicPlayHistory> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SongId).HasMaxLength(128);
        builder.Property(x => x.Title).HasMaxLength(512);
        builder.Property(x => x.Artist).HasMaxLength(512);
        builder.Property(x => x.Album).HasMaxLength(512);
        builder.Property(x => x.CoverArtUrl).HasMaxLength(512);
        builder.HasIndex(x => new { x.UserId, x.PlayedAt });
    }
}

public class MusicSubscriptionConfiguration : ModuleEntityConfiguration<MusicSubscription>
{
    public override void Configure(EntityTypeBuilder<MusicSubscription> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Source).HasMaxLength(32);
        builder.Property(x => x.Name).HasMaxLength(512);
        builder.Property(x => x.ArtUrl).HasMaxLength(1024);
        builder.Property(x => x.NavidromePlaylistId).HasMaxLength(128);
        builder.Property(x => x.LastError).HasMaxLength(1024);
        builder.Property(x => x.TracksJson).HasMaxLength(200000);
        builder.HasIndex(x => x.OwnerUserId);
        builder.HasIndex(x => new { x.Source, x.SourcePlaylistId, x.OwnerUserId }).IsUnique();
    }
}
