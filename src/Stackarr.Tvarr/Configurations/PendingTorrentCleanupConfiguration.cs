using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stackarr.Core;
using Stackarr.Tvarr.Models;

namespace Stackarr.Tvarr.Configurations;

public class PendingTorrentCleanupConfiguration : ModuleEntityConfiguration<PendingTorrentCleanup>
{
    public override void Configure(EntityTypeBuilder<PendingTorrentCleanup> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TorrentHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.TorrentName).HasMaxLength(512).IsRequired();
        builder.Property(x => x.TargetService).HasMaxLength(64).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(1024);
        builder.HasIndex(x => new { x.TorrentHash, x.TargetService }).IsUnique();
        builder.HasIndex(x => x.CompletedAt);
    }
}
