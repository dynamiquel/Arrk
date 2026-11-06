using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Arrk.Core;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Configurations;

public class TvUploadRecordConfiguration : ModuleEntityConfiguration<TvUploadRecord>
{
    public override void Configure(EntityTypeBuilder<TvUploadRecord> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.BatchId).HasMaxLength(64).IsRequired();
        builder.Property(x => x.FileName).HasMaxLength(512).IsRequired();
        builder.Property(x => x.StoredPath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.ContainerPath).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(2048);
        builder.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        builder.Property(x => x.MediaType).HasMaxLength(16);
        builder.HasIndex(x => x.BatchId);
        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => x.Status);
    }
}
