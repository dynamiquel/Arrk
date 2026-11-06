using Arrk.TestSupport;
using Arrk.Core.Services;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Arrk.Core;
using Arrk.Auth.Abstractions;

namespace Arrk.Ui.Tests;

/// <summary>
/// Pins the UI type scale. Body text and tabs are 1rem; every size is relative to the root so
/// the account's Small/Medium/Large preference scales the whole shell, and no visible text drops
/// below the 0.75rem floor (icon glyphs are exempt).
/// </summary>
public class CssTypographyTests
{
    static IEnumerable<string> ProjectFiles(string pattern)
    {
        // A live app can leave runtime data under src/<host>/config; skip anything unreadable
        // instead of failing the audit on host files.
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        return Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), pattern, options)
            .Where(path => !path.Contains("lib/bootstrap")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    static IEnumerable<string> ReadLinesSafe(string path)
    {
        try
        {
            return File.ReadAllLines(path);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    static bool IsIconRule(string selector) =>
        selector.Contains("icon", StringComparison.OrdinalIgnoreCase)
        || selector.Contains("brand-mask", StringComparison.OrdinalIgnoreCase);

    [Test]
    public async Task EveryStylesheetSizeIsRelativeAndAboveTheFloor()
    {
        Regex size = new(@"font-size:\s*([0-9.]+)(rem|em|px)");
        List<string> failures = [];
        foreach (string path in ProjectFiles("*.css"))
        {
            string selector = "";
            string[] lines = ReadLinesSafe(path).ToArray();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.Trim();
                if (trimmed.EndsWith('{'))
                    selector = trimmed[..^1].Trim();

                Match match = size.Match(line);
                if (!match.Success)
                    continue;

                double value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                string unit = match.Groups[2].Value;
                bool isRoot = selector.StartsWith("html", StringComparison.Ordinal);
                bool tooSmall = unit == "px" ? value < 12 : value < 0.75;
                bool isPx = unit == "px";

                if ((isPx && !isRoot) || (tooSmall && !IsIconRule(selector) && !isRoot))
                    failures.Add($"{Path.GetRelativePath(RepoPaths.Root, path)}:{i + 1} {selector} → {match.Value}");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task InlineRazorTextSizesRespectTheFloor()
    {
        Regex size = new(@"font-size:\s*([0-9.]+)(rem|em|px)");
        List<string> failures = [];
        foreach (string path in ProjectFiles("*.razor"))
        {
            string[] lines = ReadLinesSafe(path).ToArray();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Contains("MudIcon", StringComparison.Ordinal))
                    continue;

                Match match = size.Match(line);
                if (!match.Success)
                    continue;

                double value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                string unit = match.Groups[2].Value;
                if ((unit == "px" ? value < 12 : value < 0.75))
                    failures.Add($"{Path.GetRelativePath(RepoPaths.Root, path)}:{i + 1} {match.Value}");
            }
        }

        await Assert.That(failures).IsEmpty();
    }

    [Test]
    public async Task TextSizePreference_RoundTripsPerUser()
    {
        using TestDb database = new();
        UiTextSizePreference preference = new(new SettingsService(database));

        await Assert.That(await preference.GetAsync(7)).IsEqualTo(UiTextSize.Medium);

        await preference.SetAsync(7, UiTextSize.Large);
        await preference.SetAsync(8, UiTextSize.Small);

        await Assert.That(await preference.GetAsync(7)).IsEqualTo(UiTextSize.Large);
        await Assert.That(await preference.GetAsync(8)).IsEqualTo(UiTextSize.Small);
        await Assert.That(await preference.GetAsync(9)).IsEqualTo(UiTextSize.Medium);
    }

    [Test]
    public async Task TextSizeValuesMapToCssAndBack()
    {
        await Assert.That(UiTextSizePreference.ToValue(UiTextSize.Small)).IsEqualTo("small");
        await Assert.That(UiTextSizePreference.ToValue(UiTextSize.Medium)).IsEqualTo("medium");
        await Assert.That(UiTextSizePreference.ToValue(UiTextSize.Large)).IsEqualTo("large");
        await Assert.That(UiTextSizePreference.Parse("large")).IsEqualTo(UiTextSize.Large);
        await Assert.That(UiTextSizePreference.Parse("small")).IsEqualTo(UiTextSize.Small);
        await Assert.That(UiTextSizePreference.Parse("nonsense")).IsEqualTo(UiTextSize.Medium);
        await Assert.That(UiTextSizePreference.Parse(null)).IsEqualTo(UiTextSize.Medium);
    }

    sealed class TestDb : IDbContextFactory<ArrkDbContext>, IDisposable
    {
        readonly SqliteConnection _connection;
        readonly DbContextOptions<ArrkDbContext> _options;

        public TestDb()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<ArrkDbContext>().UseSqlite(_connection).Options;
            using ArrkDbContext db = CreateDbContext();
            db.Database.EnsureCreated();
        }

        public ArrkDbContext CreateDbContext() => new(_options, []);

        public void Dispose() => _connection.Dispose();
    }
}
