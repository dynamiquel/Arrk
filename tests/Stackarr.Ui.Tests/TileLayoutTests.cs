using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Stackarr.Auth.Abstractions;
using Stackarr.Core;
using Stackarr.Core.Services;
using Stackarr.Ui.Components;

namespace Stackarr.Ui.Tests;

/// <summary>
/// Covers the tile catalogue and dashboard document: contextual titles, config/role filtering, the
/// curated default, YAML round-tripping, validation and per-account CSS storage.
/// </summary>
public class TileLayoutTests
{
    sealed class FakeTileProvider(params TileDefinition[] tiles) : ITileProvider
    {
        public IReadOnlyList<TileDefinition> Tiles { get; } = tiles;
    }

    sealed class TestDbContextFactory(DbContextOptions<StackarrDbContext> options)
        : IDbContextFactory<StackarrDbContext>
    {
        public StackarrDbContext CreateDbContext() => new(options, []);

        public Task<StackarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }

    static IConfiguration Config(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

    static (DashboardLayoutService Layouts, UserCssService Css, SqliteConnection Connection) Build(
        IConfiguration? configuration = null)
    {
        SqliteConnection connection = new("Data Source=:memory:");
        connection.Open();
        DbContextOptions<StackarrDbContext> options = new DbContextOptionsBuilder<StackarrDbContext>()
            .UseSqlite(connection)
            .Options;
        using (StackarrDbContext db = new(options, []))
        {
            db.Database.EnsureCreated();
            db.Users.AddRange(
                new StackarrUser { Id = 7, UserName = "user7", NormalizedUserName = "USER7", DisplayName = "User 7" },
                new StackarrUser { Id = 8, UserName = "user8", NormalizedUserName = "USER8", DisplayName = "User 8" });
            db.SaveChanges();
        }

        TestDbContextFactory factory = new(options);
        UserUiPreferenceService preferences = new(factory);

        FakeTileProvider provider = new(
            new("system.host", "System", "Host", "", typeof(StatTile),
                Order: 10, DefaultSection: "Overview", DefaultOrder: 10),
            new("torrents.download", "Torrents", "Download", "", typeof(StatTile),
                Order: 20, DefaultSection: "Overview", DefaultOrder: 20, Href: "/downloads"),
            new("admin.secret", "System", "Secret", "", typeof(StatTile),
                Order: 30, AdminOnly: true, DefaultSection: "Overview", DefaultOrder: 30),
            new("tv.movies", "TV", "Movies", "", typeof(StatTile),
                Order: 40, EnabledConfigPath: "services:tv"));

        TileRegistry registry = new([provider], configuration ?? Config());
        DashboardLayoutService layouts = new(preferences, registry);
        UserCssService css = new(preferences, []);
        return (layouts, css, connection);
    }

    [Test]
    public async Task TileTitle_AddsTheCategoryOnlyOnTheDashboard()
    {
        TileDefinition tile = new("torrents.download", "Torrents", "Download", "", typeof(StatTile));

        await Assert.That(tile.TitleFor(TilePlacement.Dashboard)).IsEqualTo("TORRENTS - DOWNLOAD");
        await Assert.That(tile.TitleFor(TilePlacement.Page)).IsEqualTo("Download");
    }

    [Test]
    public async Task Registry_HidesDisabledConfigAndAdminTiles()
    {
        (DashboardLayoutService layouts, _, SqliteConnection connection) = Build(
            Config(("services:tv:enabled", "false")));
        await using (connection)
        {
            await Assert.That(layouts.FindTile("tv.movies")).IsNull();
            await Assert.That(layouts.AvailableTiles(isAdmin: false).Any(t => t.Id == "admin.secret")).IsFalse();
            await Assert.That(layouts.AvailableTiles(isAdmin: true).Any(t => t.Id == "admin.secret")).IsTrue();
        }
    }

    [Test]
    public async Task Schema_DescribesTileIdsAndStandardSizes()
    {
        (DashboardLayoutService layouts, _, SqliteConnection connection) = Build();
        await using (connection)
        {
            string schema = layouts.BuildSchema(isAdmin: false);
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(schema);
            System.Text.Json.JsonElement root = document.RootElement;
            System.Text.Json.JsonElement tile =
                root.GetProperty("properties").GetProperty("sections").GetProperty("items")
                    .GetProperty("properties").GetProperty("tiles").GetProperty("items");

            string[] ids = [.. tile.GetProperty("properties").GetProperty("id").GetProperty("enum")
                .EnumerateArray().Select(x => x.GetString()!)];
            string[] sizes = [.. tile.GetProperty("properties").GetProperty("size").GetProperty("enum")
                .EnumerateArray().Select(x => x.GetString()!)];

            await Assert.That(ids).Contains("system.host");
            await Assert.That(ids).Contains("torrents.download");
            await Assert.That(sizes).IsEquivalentTo(["Small", "Medium", "Large"]);
        }
    }

    [Test]
    public async Task DefaultLayout_IsTheSlimOverview()
    {
        (DashboardLayoutService layouts, _, SqliteConnection connection) = Build();
        await using (connection)
        {
            DashboardLayout layout = layouts.CreateDefault(isAdmin: false);

            await Assert.That(layout.Sections.Select(s => s.Title)).IsEquivalentTo(["Overview"]);
            await Assert.That(layout.Sections[0].Tiles.Select(t => t.Id))
                .IsEquivalentTo(["system.host", "torrents.download"]);
            await Assert.That(layout.Sections[0].Tiles.All(t => t.Size == TileSize.Medium)).IsTrue();
        }
    }

    [Test]
    public async Task LayoutYaml_RoundTripsSizes()
    {
        (DashboardLayoutService layouts, _, SqliteConnection connection) = Build();
        await using (connection)
        {
            DashboardLayout original = new()
            {
                Sections =
                [
                    new()
                    {
                        Id = "system",
                        Title = "System",
                        Tiles = [new() { Id = "system.host", Size = TileSize.Medium }]
                    }
                ]
            };

            string yaml = layouts.Serialize(original);
            DashboardLayout parsed = layouts.ParseAndValidate(yaml, isAdmin: false);

            await Assert.That(parsed.Sections.Single().Title).IsEqualTo("System");
            await Assert.That(parsed.Sections.Single().Tiles.Single().Id).IsEqualTo("system.host");
            await Assert.That(parsed.Sections.Single().Tiles.Single().Size).IsEqualTo(TileSize.Medium);
        }
    }

    [Test]
    public async Task Validation_RejectsUnknownAndDuplicateTiles()
    {
        (DashboardLayoutService layouts, _, SqliteConnection connection) = Build();
        await using (connection)
        {
            DashboardLayout layout = new()
            {
                Sections =
                [
                    new()
                    {
                        Id = "one",
                        Title = "One",
                        Tiles =
                        [
                            new() { Id = "system.host" },
                            new() { Id = "system.host" },
                            new() { Id = "missing.tile" }
                        ]
                    }
                ]
            };

            IReadOnlyList<DashboardLayoutError> errors = layouts.Validate(layout, isAdmin: false);

            await Assert.That(errors.Any(e => e.Message.Contains("appears more than once"))).IsTrue();
            await Assert.That(errors.Any(e => e.Message.Contains("Unknown tile"))).IsTrue();
        }
    }

    [Test]
    public async Task SavedLayout_IsLoadedForTheSameAccountOnly()
    {
        (DashboardLayoutService layouts, _, SqliteConnection connection) = Build();
        await using (connection)
        {
            DashboardLayout layout = new()
            {
                Sections =
                [
                    new() { Id = "custom", Title = "Custom", Tiles = [new() { Id = "system.host" }] }
                ]
            };

            await layouts.SaveAsync(userId: 7, layout, isAdmin: false);
            DashboardLayout loaded = await layouts.GetAsync(userId: 7, isAdmin: false);
            DashboardLayout other = await layouts.GetAsync(userId: 8, isAdmin: false);

            await Assert.That(loaded.Sections.Single().Title).IsEqualTo("Custom");
            await Assert.That(other.Sections.Select(s => s.Title)).IsEquivalentTo(["Overview"]);
        }
    }

    [Test]
    public async Task CustomCss_IsPerAccountAndEscapesTheStyleElement()
    {
        (_, UserCssService css, SqliteConnection connection) = Build();
        await using (connection)
        {
            await css.SetAsync(7, ":root { --st-pink: #fff; }</style><script>alert(1)</script>");

            string saved = await css.GetAsync(7);
            string empty = await css.GetAsync(8);

            await Assert.That(saved).Contains("--st-pink");
            await Assert.That(saved).DoesNotContain("</style");
            await Assert.That(empty).IsEqualTo("");
        }
    }
}
