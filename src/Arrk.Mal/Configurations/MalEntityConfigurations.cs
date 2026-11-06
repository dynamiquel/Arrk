using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Arrk.Core;
using Arrk.Auth.Abstractions;
using Arrk.Mal.Models;

namespace Arrk.Mal.Configurations;

public sealed class MalAccountConfiguration : ModuleEntityConfiguration<MalAccount>
{
    public override void Configure(EntityTypeBuilder<MalAccount> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.MalUsername).HasMaxLength(128).IsRequired();
        builder.Property(x => x.AccessToken).IsRequired();
        builder.Property(x => x.RefreshToken).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(1024);
        builder.Property(x => x.LastSyncSummary).HasMaxLength(1024);
        builder.HasIndex(x => x.UserId).IsUnique();
        builder.HasIndex(x => x.MalUserId).IsUnique();
        builder.HasOne<ArrkUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MalListEntryConfiguration : ModuleEntityConfiguration<MalListEntry>
{
    public override void Configure(EntityTypeBuilder<MalListEntry> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Status).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Tags).HasMaxLength(512);
        builder.Property(x => x.Comments).HasMaxLength(2048);
        builder.HasIndex(x => new { x.UserId, x.MalId }).IsUnique();
        builder.HasIndex(x => x.Status);
        builder.HasOne<ArrkUser>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MalCatalogConfiguration : ModuleEntityConfiguration<MalCatalogEntry>
{
    public override void Configure(EntityTypeBuilder<MalCatalogEntry> builder)
    {
        builder.HasKey(x => x.MalId);
        builder.Property(x => x.Title).HasMaxLength(512).IsRequired();
        builder.Property(x => x.EnglishTitle).HasMaxLength(512);
        builder.Property(x => x.JapaneseTitle).HasMaxLength(512);
        builder.Property(x => x.Synonyms).HasMaxLength(1024);
        builder.Property(x => x.RelatedAnimeJson).HasMaxLength(16384);
        builder.Property(x => x.MainPictureUrl).HasMaxLength(1024);
        builder.Property(x => x.MediaType).HasMaxLength(32);
        builder.Property(x => x.AiringStatus).HasMaxLength(32);
        builder.Property(x => x.StartDate).HasMaxLength(32);
        builder.Property(x => x.EndDate).HasMaxLength(32);
        builder.Property(x => x.Synopsis).HasMaxLength(16384);
        builder.Property(x => x.Genres).HasMaxLength(1024);
        builder.Property(x => x.Studios).HasMaxLength(1024);
        builder.Property(x => x.TmdbKind).HasMaxLength(16);
        builder.Property(x => x.PosterUrl).HasMaxLength(1024);
        builder.Property(x => x.DisplayTitle).HasMaxLength(512);
        builder.Property(x => x.DisplayOverview).HasMaxLength(16384);
        builder.HasIndex(x => x.TmdbId);
    }
}

public sealed class MalMapConfiguration : ModuleEntityConfiguration<MalMap>
{
    public override void Configure(EntityTypeBuilder<MalMap> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).HasMaxLength(16).IsRequired();
        builder.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        builder.Property(x => x.State).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => new { x.Provider, x.ProviderId, x.Kind, x.Season }).IsUnique();
        builder.HasMany(x => x.Parts)
            .WithOne(x => x.Map)
            .HasForeignKey(x => x.MalMapId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MalMapPartConfiguration : ModuleEntityConfiguration<MalMapPart>
{
    public override void Configure(EntityTypeBuilder<MalMapPart> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Source).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.MalId);
        builder.HasIndex(x => x.MalMapId);
    }
}
