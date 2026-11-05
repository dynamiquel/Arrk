using Stackarr.Media.Abstractions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stackarr.Core;
using Stackarr.Requests.Models;

namespace Stackarr.Requests.Configurations;

public class RequestBlacklistConfiguration : ModuleEntityConfiguration<RequestBlacklist>
{
    public override void Configure(EntityTypeBuilder<RequestBlacklist> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Domain)
            .HasConversion(v => v.Value, v => new MediaDomain(v))
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(x => x.ProviderIds).HasMaxLength(2048).IsRequired();
        builder.Property(x => x.Title).HasMaxLength(512).IsRequired();
        builder.Property(x => x.Reason).HasMaxLength(1024);
        builder.HasIndex(x => x.Domain);
        builder.HasIndex(x => x.BlacklistedAt);
    }
}
