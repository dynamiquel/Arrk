using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stackarr.Core;

namespace Stackarr.Ai.Data;

public sealed class AiDecisionOutcomeConfiguration : ModuleEntityConfiguration<AiDecisionOutcome>
{
    public override void Configure(EntityTypeBuilder<AiDecisionOutcome> builder)
    {
        builder.ToTable("AiDecisions");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Feature).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Operation).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Subject).HasMaxLength(128).IsRequired();
        builder.Property(x => x.Mode).HasMaxLength(32).IsRequired();
        builder.Property(x => x.DeterministicChoice).HasMaxLength(512).IsRequired();
        builder.Property(x => x.AiChoice).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(512);
        builder.Property(x => x.Request).HasMaxLength(512);
        builder.Property(x => x.Context).HasMaxLength(8000);
        builder.Property(x => x.Outcome).HasMaxLength(64);
        builder.HasIndex(x => x.Timestamp);
        builder.HasIndex(x => new { x.Feature, x.Subject, x.Timestamp });
    }
}
