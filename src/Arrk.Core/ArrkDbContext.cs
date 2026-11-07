using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Arrk.Auth.Abstractions;
using Arrk.Core.Services;
using Arrk.Core.Lifecycle;
using Arrk.Core.Models;
using Arrk.Notifications;
using Arrk.Core.Playback;

namespace Arrk.Core;

/// <summary>
/// Arrk's database. Hosts ASP.NET Core Identity (accounts/roles/external logins), OpenIddict
/// (the OIDC provider state) and Arrk's own entities.
/// </summary>
public class ArrkDbContext(
    DbContextOptions<ArrkDbContext> options,
    IEnumerable<IModuleEntityConfiguration> moduleConfigurations)
    : IdentityDbContext<ArrkUser, IdentityRole<int>, int>(options)
{
    public DbSet<ServiceHealth> ServiceHealth { get; set; } = null!;
    public DbSet<Notification> Notifications { get; set; } = null!;
    public DbSet<NotificationSubscription> NotificationSubscriptions { get; set; } = null!;
    public DbSet<UserNotificationChannel> UserNotificationChannels { get; set; } = null!;
    public DbSet<DownloadState> DownloadStates { get; set; } = null!;
    public DbSet<ContainerStats> ContainerStats { get; set; } = null!;
    public DbSet<SpeedtestResult> SpeedtestResults { get; set; } = null!;
    public DbSet<SystemEvent> SystemEvents { get; set; } = null!;
    public DbSet<UserProviderLink> UserProviderLinks { get; set; } = null!;
    public DbSet<ArrkSetting> Settings { get; set; } = null!;
    public DbSet<UserUiPreference> UserUiPreferences { get; set; } = null!;
    public DbSet<WatchActivityEntry> WatchActivity { get; set; } = null!;
    public DbSet<MediaConsumptionStamp> MediaConsumption { get; set; } = null!;
    public DbSet<Watchlist> Watchlists { get; set; } = null!;
    public DbSet<WatchlistItem> WatchlistItems { get; set; } = null!;
    public DbSet<MediaPin> MediaPins { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.UseOpenIddict();

        modelBuilder.Entity<ArrkUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.AvatarContentType).HasMaxLength(128);
        });

        modelBuilder.Entity<ArrkSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(512);
        });

        modelBuilder.Entity<UserUiPreference>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).ValueGeneratedNever();
            // Dashboard layouts and custom CSS are free-form documents; SQLite stores TEXT, so the
            // cap is only a guardrail against accidental oversized writes.
            e.Property(x => x.DashboardYaml).HasMaxLength(131_072);
            e.Property(x => x.CustomCss).HasMaxLength(131_072);
            e.HasOne(x => x.User)
                .WithOne()
                .HasForeignKey<UserUiPreference>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserProviderLink>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.ExternalId).HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.Provider, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User)
                .WithMany(u => u.ProviderLinks)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ServiceHealth>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ServiceName).HasMaxLength(256);
            e.HasIndex(x => x.ServiceName);
            e.HasIndex(x => x.CheckedAt);
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(128).IsRequired();
            e.Property(x => x.Category).HasMaxLength(64).IsRequired();
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.Body).HasMaxLength(2048);
            e.Property(x => x.Severity).HasMaxLength(32).IsRequired();
            e.Property(x => x.Url).HasMaxLength(1024);
            e.Property(x => x.Source).HasMaxLength(256);
            e.Property(x => x.SourceName).HasMaxLength(512);
            e.Property(x => x.Data).HasMaxLength(4096);
            e.Property(x => x.Channels).HasMaxLength(256);
            e.Property(x => x.Error).HasMaxLength(1024);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.Key);
            e.HasIndex(x => x.ReadAt);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<NotificationSubscription>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderKey).HasMaxLength(32).IsRequired();
            e.Property(x => x.Domain)
                .HasConversion(v => v.Value, v => new MediaDomain(v))
                .HasMaxLength(32)
                .IsRequired();
            e.Property(x => x.ItemKey).HasMaxLength(512).IsRequired();
            e.Property(x => x.Source).HasMaxLength(32).IsRequired();
            e.Property(x => x.SourceId).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(512).IsRequired();
            e.Property(x => x.Subtitle).HasMaxLength(512);
            e.Property(x => x.PosterUrl).HasMaxLength(1024);
            e.HasIndex(x => new { x.UserId, x.ProviderKey, x.ItemKey }).IsUnique();
            e.HasIndex(x => new { x.ProviderKey, x.ItemKey });
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<UserNotificationChannel>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Channel).HasMaxLength(32).IsRequired();
            e.Property(x => x.Destination).HasMaxLength(128);
            e.Property(x => x.LastError).HasMaxLength(1024);
            e.HasIndex(x => new { x.UserId, x.Channel }).IsUnique();
            e.HasIndex(x => x.Channel);
        });

        modelBuilder.Entity<DownloadState>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Hash).HasMaxLength(64);
            e.HasIndex(x => x.Hash);
            e.HasIndex(x => x.Category);
        });

        modelBuilder.Entity<ContainerStats>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ContainerId).HasMaxLength(128);
            e.Property(x => x.ContainerName).HasMaxLength(256);
            e.HasIndex(x => new { x.ContainerName, x.Timestamp });
        });

        modelBuilder.Entity<SpeedtestResult>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Target).HasMaxLength(32);
            e.HasIndex(x => x.TestedAt);
            e.HasIndex(x => x.Target);
        });

        modelBuilder.Entity<SystemEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Category).HasMaxLength(64).IsRequired();
            e.Property(x => x.EventType).HasMaxLength(128).IsRequired();
            e.Property(x => x.Topic).HasMaxLength(128);
            e.Property(x => x.Severity).HasMaxLength(32).IsRequired();
            e.Property(x => x.Message).HasMaxLength(2048).IsRequired();
            e.Property(x => x.Source).HasMaxLength(256);
            e.Property(x => x.SourceName).HasMaxLength(512);
            e.Property(x => x.Data).HasMaxLength(4096);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.Category);
            e.HasIndex(x => x.EventType);
            e.HasIndex(x => x.Topic);
            e.HasIndex(x => new { x.Category, x.Timestamp });
        });

        modelBuilder.Entity<WatchActivityEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Domain)
                .HasConversion(v => v.Value, v => new MediaDomain(v))
                .HasMaxLength(32)
                .IsRequired();
            e.Property(x => x.Username).HasMaxLength(128).IsRequired();
            e.Property(x => x.ProviderKey).HasMaxLength(32).IsRequired();
            e.Property(x => x.ItemType).HasMaxLength(32).IsRequired();
            e.Property(x => x.ItemKey).HasMaxLength(512).IsRequired();
            e.Property(x => x.ParentItemKey).HasMaxLength(512);
            e.Property(x => x.Title).HasMaxLength(512).IsRequired();
            e.Property(x => x.Subtitle).HasMaxLength(512);
            e.Property(x => x.ImageUrl).HasMaxLength(1024);
            e.Property(x => x.Source).HasMaxLength(64).IsRequired();
            e.Property(x => x.Device).HasMaxLength(256);
            e.Property(x => x.Client).HasMaxLength(256);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.SessionKey).HasMaxLength(256);
            e.HasIndex(x => new { x.Username, x.StartedAt });
            e.HasIndex(x => new { x.ProviderKey, x.StartedAt });
            e.HasIndex(x => new { x.ProviderKey, x.ItemKey, x.StartedAt });
            e.HasIndex(x => new { x.ProviderKey, x.ParentItemKey, x.StartedAt });
            e.HasIndex(x => x.SessionKey);
        });

        modelBuilder.Entity<MediaConsumptionStamp>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderKey).HasMaxLength(32).IsRequired();
            e.Property(x => x.ItemKey).HasMaxLength(512).IsRequired();
            e.Property(x => x.ItemType).HasMaxLength(32).IsRequired();
            e.Property(x => x.ParentItemKey).HasMaxLength(512);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.ImageUrl).HasMaxLength(1024);
            e.Property(x => x.LastUsername).HasMaxLength(128);
            e.Property(x => x.LastSource).HasMaxLength(64);
            e.Property(x => x.LastDevice).HasMaxLength(256);
            e.HasIndex(x => new { x.ProviderKey, x.ItemKey }).IsUnique();
            e.HasIndex(x => x.LastConsumedAt);
        });

        modelBuilder.Entity<Watchlist>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(WatchlistLimits.MaxNameLength).IsRequired().UseCollation("NOCASE");
            e.Property(x => x.Description).HasMaxLength(512);
            e.HasIndex(x => new { x.UserId, x.Name }).IsUnique();
            e.HasMany(x => x.Items)
                .WithOne(x => x.Watchlist)
                .HasForeignKey(x => x.WatchlistId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WatchlistItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Domain)
                .HasConversion(v => v.Value, v => new MediaDomain(v))
                .HasMaxLength(32)
                .IsRequired();
            e.Property(x => x.ProviderKey).HasMaxLength(32).IsRequired();
            e.Property(x => x.ItemKey).HasMaxLength(512).IsRequired();
            e.Property(x => x.Title).HasMaxLength(512).IsRequired();
            e.Property(x => x.Subtitle).HasMaxLength(512);
            e.Property(x => x.PosterUrl).HasMaxLength(1024);
            e.HasIndex(x => new { x.WatchlistId, x.ItemKey }).IsUnique();
            e.HasIndex(x => new { x.ProviderKey, x.ItemKey });
        });

        modelBuilder.Entity<MediaPin>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderKey).HasMaxLength(32).IsRequired();
            e.Property(x => x.ItemKey).HasMaxLength(512).IsRequired();
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.PinnedBy).HasMaxLength(128);
            e.HasIndex(x => new { x.ProviderKey, x.ItemKey }).IsUnique();
        });

        // Apply entity configurations contributed by feature modules.
        foreach (IModuleEntityConfiguration configuration in moduleConfigurations)
        {
            configuration.Configure(modelBuilder);
        }
    }
}
