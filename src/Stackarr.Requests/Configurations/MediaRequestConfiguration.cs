using Stackarr.Media.Abstractions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stackarr.Core;
using Stackarr.Requests.Models;

namespace Stackarr.Requests.Configurations;

public class MediaRequestConfiguration : ModuleEntityConfiguration<MediaRequest>
{
    public override void Configure(EntityTypeBuilder<MediaRequest> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Domain)
            .HasConversion(v => v.Value, v => new MediaDomain(v))
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(x => x.RequestedByUsername).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ProviderIds).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(512).IsRequired();
        builder.Property(x => x.HandlerKey).HasMaxLength(64).IsRequired();
        builder.Property(x => x.QualityKey).HasMaxLength(128);
        builder.Property(x => x.QualityName).HasMaxLength(256);
        builder.Property(x => x.DecisionReason).HasMaxLength(1024);
        builder.Property(x => x.LibraryId).HasMaxLength(128);
        builder.Property(x => x.ProviderKey).HasMaxLength(32);
        builder.Property(x => x.EntityKey).HasMaxLength(512);
        builder.HasIndex(x => x.RequestedByUserId);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.RequestedAt);
        builder.HasIndex(x => new { x.Domain, x.Kind });
    }
}
