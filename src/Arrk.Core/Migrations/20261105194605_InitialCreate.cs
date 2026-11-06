using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Arrk.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiCache",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Feature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCache", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AiDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Feature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Request = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Context = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DeterministicChoice = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AiChoice = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Applied = table.Column<bool>(type: "INTEGER", nullable: false),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OutcomeAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiDecisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiUsage",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Feature = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PromptTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletionTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    CacheHit = table.Column<bool>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    PromptText = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseText = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUsage", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AiUserPreference",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PersonaKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiUserPreference", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Avatar = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AvatarContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: true),
                    SecurityStamp = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumber = table.Column<string>(type: "TEXT", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ContainerStats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ContainerId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CpuPercent = table.Column<double>(type: "REAL", nullable: false),
                    MemoryUsageBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryLimitBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    MemoryPercent = table.Column<double>(type: "REAL", nullable: false),
                    NetworkRxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    NetworkTxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DownloadStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    DownloadSpeed = table.Column<double>(type: "REAL", nullable: false),
                    UploadSpeed = table.Column<double>(type: "REAL", nullable: false),
                    Ratio = table.Column<double>(type: "REAL", nullable: false),
                    Seeds = table.Column<int>(type: "INTEGER", nullable: false),
                    Peers = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastChecked = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SlowStrikeCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GrabRecord",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlbumId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Identifier = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ReleaseTitle = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TorrentId = table.Column<long>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeezerTrackId = table.Column<long>(type: "INTEGER", nullable: false),
                    DeezerAlbumId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrabRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryIntent",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    QualityProfileId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryIntent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LifecycleItemState",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ItemType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastNotifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LifecycleItemState", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MalCatalogEntry",
                columns: table => new
                {
                    MalId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    EnglishTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    JapaneseTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Synonyms = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RelatedAnimeJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    MainPictureUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    NumEpisodes = table.Column<int>(type: "INTEGER", nullable: false),
                    AiringStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    StartDate = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    EndDate = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Synopsis = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    Genres = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Studios = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Mean = table.Column<double>(type: "REAL", nullable: true),
                    Rank = table.Column<int>(type: "INTEGER", nullable: true),
                    Nsfw = table.Column<bool>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: true),
                    TmdbKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TmdbSeason = table.Column<int>(type: "INTEGER", nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DisplayTitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    DisplayOverview = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EnrichedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MalCatalogEntry", x => x.MalId);
                });

            migrationBuilder.CreateTable(
                name: "MalMap",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ProviderId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Season = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LocalEpisodeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MalMap", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaConsumption",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ItemType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ParentItemKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastConsumedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastSource = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastDevice = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaConsumption", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaPins",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    PinnedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PinnedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaPins", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MediaRequest",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RequestedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedByUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderIds = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    HandlerKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    QualityKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    QualityName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DecisionReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LibraryId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EntityKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    NotifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRequest", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MusicAcquireQueueItem",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    AlbumId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeezerTrackId = table.Column<long>(type: "INTEGER", nullable: false),
                    RequireArtist = table.Column<bool>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    InFlight = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicAcquireQueueItem", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MusicArtist",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    SortName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    DeezerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Overview = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    GenresJson = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    Added = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    FanartUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicArtist", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MusicPlayHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    SongId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Album = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CoverArtUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    PlayedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlayHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MusicPlaylistOwner",
                columns: table => new
                {
                    PlaylistId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    OwnerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicPlaylistOwner", x => x.PlaylistId);
                });

            migrationBuilder.CreateTable(
                name: "MusicSubscription",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourcePlaylistId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ArtUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    AutoDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    NavidromePlaylistId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TrackCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    TracksJson = table.Column<string>(type: "TEXT", maxLength: 200000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicSubscription", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Data = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReadAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Delivered = table.Column<bool>(type: "INTEGER", nullable: false),
                    Channels = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationSubscriptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    IncludeDescendants = table.Column<bool>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    NotifyStatus = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyDownload = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyNewContent = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyProblems = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyLifecycle = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastNotifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSubscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictApplications",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ApplicationType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "TEXT", nullable: true),
                    ClientType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayNames = table.Column<string>(type: "TEXT", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "TEXT", nullable: true),
                    Permissions = table.Column<string>(type: "TEXT", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "TEXT", nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    RedirectUris = table.Column<string>(type: "TEXT", nullable: true),
                    Requirements = table.Column<string>(type: "TEXT", nullable: true),
                    Settings = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictScopes",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Descriptions = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    DisplayNames = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    Resources = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingTorrentCleanup",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TorrentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TorrentName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetService = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingTorrentCleanup", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequestBlacklist",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ProviderIds = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    BlacklistedByUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlacklistedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestBlacklist", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServiceHealth",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServiceName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    IsHealthy = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceHealth", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SpeedtestResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DownloadMbps = table.Column<double>(type: "REAL", nullable: false),
                    UploadMbps = table.Column<double>(type: "REAL", nullable: false),
                    PingMs = table.Column<double>(type: "REAL", nullable: false),
                    JitterMs = table.Column<double>(type: "REAL", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TestedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpeedtestResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Topic = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Severity = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Data = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TvUploadRecord",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BatchId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    StoredPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ContainerPath = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    TvdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TvUploadRecord", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserNotificationChannels",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Destination = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    VerifiedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotificationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WatchActivity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ParentItemKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Device = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    Client = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SessionKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PositionTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    RuntimeTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    TvdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    SeasonNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    LibraryId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchActivity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Watchlists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false, collation: "NOCASE"),
                    Description = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Watchlists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimType = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimValue = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    RoleId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    LoginProvider = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MalAccount",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MalUserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MalUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", nullable: false),
                    AccessExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LinkedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPulledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSyncedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProtectProgress = table.Column<bool>(type: "INTEGER", nullable: false),
                    NeedsReauth = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastSyncSummary = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MalAccount", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MalAccount_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MalListEntry",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    MalId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Score = table.Column<int>(type: "INTEGER", nullable: false),
                    WatchedEpisodes = table.Column<int>(type: "INTEGER", nullable: false),
                    IsRewatching = table.Column<bool>(type: "INTEGER", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Comments = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    MalUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MalListEntry", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MalListEntry_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserProviderLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SyncedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProviderLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserProviderLinks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserUiPreferences",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    DashboardYaml = table.Column<string>(type: "TEXT", maxLength: 131072, nullable: false),
                    CustomCss = table.Column<string>(type: "TEXT", maxLength: 131072, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserUiPreferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserUiPreferences_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MalMapPart",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MalMapId = table.Column<int>(type: "INTEGER", nullable: false),
                    MalId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalEpisodeStart = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalEpisodeEnd = table.Column<int>(type: "INTEGER", nullable: false),
                    MalEpisodeStart = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MalMapPart", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MalMapPart_MalMap_MalMapId",
                        column: x => x.MalMapId,
                        principalTable: "MalMap",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MusicAlbum",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ArtistId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AlbumType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReleaseDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeezerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Overview = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTrackCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Added = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IdentificationAttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RetagAttemptedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicAlbum", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicAlbum_MusicArtist_ArtistId",
                        column: x => x.ArtistId,
                        principalTable: "MusicArtist",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictAuthorizations",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    Scopes = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictAuthorizations_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WatchlistItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WatchlistId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderKey = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ItemKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Subtitle = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    PosterUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    TmdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    TvdbId = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatchlistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WatchlistItems_Watchlists_WatchlistId",
                        column: x => x.WatchlistId,
                        principalTable: "Watchlists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MusicTrack",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlbumId = table.Column<int>(type: "INTEGER", nullable: false),
                    ArtistId = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DiscNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    TrackNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    AbsoluteTrackNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    DeezerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Monitored = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasFile = table.Column<bool>(type: "INTEGER", nullable: false),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    LyricsState = table.Column<int>(type: "INTEGER", nullable: false),
                    LyricsTier = table.Column<int>(type: "INTEGER", nullable: false),
                    LyricsCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MusicTrack", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MusicTrack_MusicAlbum_AlbumId",
                        column: x => x.AlbumId,
                        principalTable: "MusicAlbum",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OpenIddictTokens",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    ApplicationId = table.Column<string>(type: "TEXT", nullable: true),
                    AuthorizationId = table.Column<string>(type: "TEXT", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    Properties = table.Column<string>(type: "TEXT", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReferenceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "OpenIddictApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "OpenIddictAuthorizations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiCache_ExpiresAt",
                table: "AiCache",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AiDecisions_Feature_Subject_Timestamp",
                table: "AiDecisions",
                columns: new[] { "Feature", "Subject", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AiDecisions_Timestamp",
                table: "AiDecisions",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_Feature_Timestamp",
                table: "AiUsage",
                columns: new[] { "Feature", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_Timestamp",
                table: "AiUsage",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_AiUsage_UserId_Timestamp",
                table: "AiUsage",
                columns: new[] { "UserId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContainerStats_ContainerName_Timestamp",
                table: "ContainerStats",
                columns: new[] { "ContainerName", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadStates_Category",
                table: "DownloadStates",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadStates_Hash",
                table: "DownloadStates",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_GrabRecord_AlbumId",
                table: "GrabRecord",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_GrabRecord_DeezerAlbumId",
                table: "GrabRecord",
                column: "DeezerAlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_GrabRecord_DeezerTrackId",
                table: "GrabRecord",
                column: "DeezerTrackId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryIntent_Kind_TargetId",
                table: "LibraryIntent",
                columns: new[] { "Kind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleItemState_Level",
                table: "LifecycleItemState",
                column: "Level");

            migrationBuilder.CreateIndex(
                name: "IX_LifecycleItemState_ProviderKey_ItemKey",
                table: "LifecycleItemState",
                columns: new[] { "ProviderKey", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MalAccount_MalUserId",
                table: "MalAccount",
                column: "MalUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MalAccount_UserId",
                table: "MalAccount",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MalCatalogEntry_TmdbId",
                table: "MalCatalogEntry",
                column: "TmdbId");

            migrationBuilder.CreateIndex(
                name: "IX_MalListEntry_Status",
                table: "MalListEntry",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MalListEntry_UserId_MalId",
                table: "MalListEntry",
                columns: new[] { "UserId", "MalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MalMap_Provider_ProviderId_Kind_Season",
                table: "MalMap",
                columns: new[] { "Provider", "ProviderId", "Kind", "Season" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MalMapPart_MalId",
                table: "MalMapPart",
                column: "MalId");

            migrationBuilder.CreateIndex(
                name: "IX_MalMapPart_MalMapId",
                table: "MalMapPart",
                column: "MalMapId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaConsumption_LastConsumedAt",
                table: "MediaConsumption",
                column: "LastConsumedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MediaConsumption_ProviderKey_ItemKey",
                table: "MediaConsumption",
                columns: new[] { "ProviderKey", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaPins_ProviderKey_ItemKey",
                table: "MediaPins",
                columns: new[] { "ProviderKey", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequest_Domain_Kind",
                table: "MediaRequest",
                columns: new[] { "Domain", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequest_RequestedAt",
                table: "MediaRequest",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequest_RequestedByUserId",
                table: "MediaRequest",
                column: "RequestedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequest_Status",
                table: "MediaRequest",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MusicAcquireQueueItem_Key",
                table: "MusicAcquireQueueItem",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbum_ArtistId",
                table: "MusicAlbum",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicAlbum_DeezerId",
                table: "MusicAlbum",
                column: "DeezerId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicArtist_DeezerId",
                table: "MusicArtist",
                column: "DeezerId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicArtist_Name",
                table: "MusicArtist",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlayHistory_UserId_PlayedAt",
                table: "MusicPlayHistory",
                columns: new[] { "UserId", "PlayedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MusicPlaylistOwner_OwnerUserId",
                table: "MusicPlaylistOwner",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicSubscription_OwnerUserId",
                table: "MusicSubscription",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicSubscription_Source_SourcePlaylistId_OwnerUserId",
                table: "MusicSubscription",
                columns: new[] { "Source", "SourcePlaylistId", "OwnerUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_AlbumId",
                table: "MusicTrack",
                column: "AlbumId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_ArtistId",
                table: "MusicTrack",
                column: "ArtistId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_DeezerId",
                table: "MusicTrack",
                column: "DeezerId");

            migrationBuilder.CreateIndex(
                name: "IX_MusicTrack_LyricsState",
                table: "MusicTrack",
                column: "LyricsState");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Category",
                table: "Notifications",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CreatedAt",
                table: "Notifications",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_Key",
                table: "Notifications",
                column: "Key");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_ReadAt",
                table: "Notifications",
                column: "ReadAt");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId",
                table: "Notifications",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSubscriptions_ProviderKey_ItemKey",
                table: "NotificationSubscriptions",
                columns: new[] { "ProviderKey", "ItemKey" });

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSubscriptions_UserId",
                table: "NotificationSubscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationSubscriptions_UserId_ProviderKey_ItemKey",
                table: "NotificationSubscriptions",
                columns: new[] { "UserId", "ProviderKey", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictApplications_ClientId",
                table: "OpenIddictApplications",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
                table: "OpenIddictAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictScopes_Name",
                table: "OpenIddictScopes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
                table: "OpenIddictTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_AuthorizationId",
                table: "OpenIddictTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_OpenIddictTokens_ReferenceId",
                table: "OpenIddictTokens",
                column: "ReferenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingTorrentCleanup_CompletedAt",
                table: "PendingTorrentCleanup",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PendingTorrentCleanup_TorrentHash_TargetService",
                table: "PendingTorrentCleanup",
                columns: new[] { "TorrentHash", "TargetService" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestBlacklist_BlacklistedAt",
                table: "RequestBlacklist",
                column: "BlacklistedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RequestBlacklist_Domain",
                table: "RequestBlacklist",
                column: "Domain");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceHealth_CheckedAt",
                table: "ServiceHealth",
                column: "CheckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceHealth_ServiceName",
                table: "ServiceHealth",
                column: "ServiceName");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedtestResults_Target",
                table: "SpeedtestResults",
                column: "Target");

            migrationBuilder.CreateIndex(
                name: "IX_SpeedtestResults_TestedAt",
                table: "SpeedtestResults",
                column: "TestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SystemEvents_Category",
                table: "SystemEvents",
                column: "Category");

            migrationBuilder.CreateIndex(
                name: "IX_SystemEvents_Category_Timestamp",
                table: "SystemEvents",
                columns: new[] { "Category", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_SystemEvents_EventType",
                table: "SystemEvents",
                column: "EventType");

            migrationBuilder.CreateIndex(
                name: "IX_SystemEvents_Timestamp",
                table: "SystemEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_SystemEvents_Topic",
                table: "SystemEvents",
                column: "Topic");

            migrationBuilder.CreateIndex(
                name: "IX_TvUploadRecord_BatchId",
                table: "TvUploadRecord",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_TvUploadRecord_CreatedAt",
                table: "TvUploadRecord",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TvUploadRecord_Status",
                table: "TvUploadRecord",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotificationChannels_Channel",
                table: "UserNotificationChannels",
                column: "Channel");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotificationChannels_UserId_Channel",
                table: "UserNotificationChannels",
                columns: new[] { "UserId", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProviderLinks_Provider_ExternalId",
                table: "UserProviderLinks",
                columns: new[] { "Provider", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProviderLinks_UserId",
                table: "UserProviderLinks",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WatchActivity_ProviderKey_ItemKey_StartedAt",
                table: "WatchActivity",
                columns: new[] { "ProviderKey", "ItemKey", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchActivity_ProviderKey_ParentItemKey_StartedAt",
                table: "WatchActivity",
                columns: new[] { "ProviderKey", "ParentItemKey", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchActivity_ProviderKey_StartedAt",
                table: "WatchActivity",
                columns: new[] { "ProviderKey", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchActivity_SessionKey",
                table: "WatchActivity",
                column: "SessionKey");

            migrationBuilder.CreateIndex(
                name: "IX_WatchActivity_Username_StartedAt",
                table: "WatchActivity",
                columns: new[] { "Username", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_ProviderKey_ItemKey",
                table: "WatchlistItems",
                columns: new[] { "ProviderKey", "ItemKey" });

            migrationBuilder.CreateIndex(
                name: "IX_WatchlistItems_WatchlistId_ItemKey",
                table: "WatchlistItems",
                columns: new[] { "WatchlistId", "ItemKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Watchlists_UserId_Name",
                table: "Watchlists",
                columns: new[] { "UserId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiCache");

            migrationBuilder.DropTable(
                name: "AiDecisions");

            migrationBuilder.DropTable(
                name: "AiUsage");

            migrationBuilder.DropTable(
                name: "AiUserPreference");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "ContainerStats");

            migrationBuilder.DropTable(
                name: "DownloadStates");

            migrationBuilder.DropTable(
                name: "GrabRecord");

            migrationBuilder.DropTable(
                name: "LibraryIntent");

            migrationBuilder.DropTable(
                name: "LifecycleItemState");

            migrationBuilder.DropTable(
                name: "MalAccount");

            migrationBuilder.DropTable(
                name: "MalCatalogEntry");

            migrationBuilder.DropTable(
                name: "MalListEntry");

            migrationBuilder.DropTable(
                name: "MalMapPart");

            migrationBuilder.DropTable(
                name: "MediaConsumption");

            migrationBuilder.DropTable(
                name: "MediaPins");

            migrationBuilder.DropTable(
                name: "MediaRequest");

            migrationBuilder.DropTable(
                name: "MusicAcquireQueueItem");

            migrationBuilder.DropTable(
                name: "MusicPlayHistory");

            migrationBuilder.DropTable(
                name: "MusicPlaylistOwner");

            migrationBuilder.DropTable(
                name: "MusicSubscription");

            migrationBuilder.DropTable(
                name: "MusicTrack");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "NotificationSubscriptions");

            migrationBuilder.DropTable(
                name: "OpenIddictScopes");

            migrationBuilder.DropTable(
                name: "OpenIddictTokens");

            migrationBuilder.DropTable(
                name: "PendingTorrentCleanup");

            migrationBuilder.DropTable(
                name: "RequestBlacklist");

            migrationBuilder.DropTable(
                name: "ServiceHealth");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SpeedtestResults");

            migrationBuilder.DropTable(
                name: "SystemEvents");

            migrationBuilder.DropTable(
                name: "TvUploadRecord");

            migrationBuilder.DropTable(
                name: "UserNotificationChannels");

            migrationBuilder.DropTable(
                name: "UserProviderLinks");

            migrationBuilder.DropTable(
                name: "UserUiPreferences");

            migrationBuilder.DropTable(
                name: "WatchActivity");

            migrationBuilder.DropTable(
                name: "WatchlistItems");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "MalMap");

            migrationBuilder.DropTable(
                name: "MusicAlbum");

            migrationBuilder.DropTable(
                name: "OpenIddictAuthorizations");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Watchlists");

            migrationBuilder.DropTable(
                name: "MusicArtist");

            migrationBuilder.DropTable(
                name: "OpenIddictApplications");
        }
    }
}
