using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stackarr.Core;

namespace Stackarr.Ai.Data;

public sealed class AiUsageRecordConfiguration : ModuleEntityConfiguration<AiUsageRecord>
{
    public override void Configure(EntityTypeBuilder<AiUsageRecord> builder)
    {
        builder.ToTable("AiUsage");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Feature).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Operation).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Model).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Error).HasMaxLength(512);
        builder.HasIndex(x => x.Timestamp);
        builder.HasIndex(x => new { x.Feature, x.Timestamp });
        builder.HasIndex(x => new { x.UserId, x.Timestamp });
    }
}

public sealed class AiCacheEntryConfiguration : ModuleEntityConfiguration<AiCacheEntry>
{
    public override void Configure(EntityTypeBuilder<AiCacheEntry> builder)
    {
        builder.ToTable("AiCache");
        builder.HasKey(x => x.Key);
        builder.Property(x => x.Key).HasMaxLength(64);
        builder.Property(x => x.Feature).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Value).IsRequired();
        builder.HasIndex(x => x.ExpiresAt);
    }
}

public sealed class AiUserPreferenceConfiguration : ModuleEntityConfiguration<AiUserPreference>
{
    public override void Configure(EntityTypeBuilder<AiUserPreference> builder)
    {
        builder.ToTable("AiUserPreference");
        builder.HasKey(x => x.UserId);
        builder.Property(x => x.PersonaKey).HasMaxLength(64).IsRequired();
    }
}
