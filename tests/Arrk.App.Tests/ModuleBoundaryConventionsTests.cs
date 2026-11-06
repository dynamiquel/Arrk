using System.IO;
using System.Text.RegularExpressions;
using Arrk.TestSupport;

namespace Arrk.App.Tests;

/// <summary>
/// Guards the shell against coupling to a module. A module's enabled state is read from the shared
/// config contract (<c>services:&lt;x&gt;:enabled</c>) through the UI registry, and each module owns
/// its navigation, so the shell never references a module type or a module's surface key.
/// </summary>
public class ModuleBoundaryConventionsTests
{
    [Test]
    public async Task App_DoesNotReferenceModuleConfigTypes()
    {
        string appRoot = Path.Combine(RepoPaths.Root, "src/Arrk.App");
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };
        string[] files =
        [
            .. Directory.EnumerateFiles(appRoot, "*.cs", options),
            .. Directory.EnumerateFiles(appRoot, "*.razor", options)
        ];

        List<string> offenders = [];
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(RepoPaths.Root, file);
            // Runtime data (mounted config/plugins) and build output (obj/bin) are not source.
            string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s is "obj" or "bin" or "config"))
                continue;

            string content = File.ReadAllText(file);
            if (Regex.IsMatch(content, @"\b(MusicConfig|TvConfig)\b"))
                offenders.Add(relative);
        }

        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task GatedSurfaces_ReadAvailabilityFromTheRegistry()
    {
        // The shell's Discover page composes its tabs from the registry; each module's own pages
        // read their availability from the same registry.
        string[] gated =
        [
            "src/Arrk.Music/Components/Pages/Music.razor",
            "src/Arrk.Tvarr/Components/Pages/Movies.razor",
            "src/Arrk.Tvarr/Components/Pages/TvShows.razor"
        ];

        foreach (string relative in gated)
        {
            string content = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
            await Assert.That(content).Contains("UiRegistry");
            await Assert.That(content).Contains("IsAvailable(");
        }

        string discover = File.ReadAllText(Path.Combine(RepoPaths.Root, "src/Arrk.App/Components/Pages/Discover.razor"));
        await Assert.That(discover).Contains("UiRegistry");
        await Assert.That(discover).Contains("DiscoverSources");
    }

    [Test]
    public async Task DiscoverPage_DoesNotReferenceModuleUi()
    {
        // The shell's Discover page is composed from registered IDiscoverSources; it must not know
        // about a specific module's pages or catalog types.
        string discover = File.ReadAllText(
            Path.Combine(RepoPaths.Root, "src/Arrk.App/Components/Pages/Discover.razor"));

        await Assert.That(discover).DoesNotContain("MediaItem");
        await Assert.That(discover).DoesNotContain("MediaCatalogService");
        await Assert.That(discover).DoesNotContain("MusicDiscover");
        await Assert.That(discover).DoesNotContain("TmdbService");
    }

    [Test]
    public async Task Shell_NeverReferencesAModuleSurface()
    {
        // The shell must not hard-code a module's surface keys; modules declare their own.
        string[] shellFiles =
        [
            "src/Arrk.App/Services/Ui/AppNavigationProvider.cs",
            "src/Arrk.App/Services/Ui/AppHeadAssetProvider.cs",
            "src/Arrk.App/Components/Layout/NavMenu.razor"
        ];

        foreach (string relative in shellFiles)
        {
            string content = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
            await Assert.That(content).DoesNotContain("SurfaceKeys");
        }
    }

    [Test]
    public async Task Navigation_IsComposedFromTheRegistry()
    {
        string navMenu = File.ReadAllText(
            Path.Combine(RepoPaths.Root, "src/Arrk.App/Components/Layout/NavMenu.razor"));

        await Assert.That(navMenu).Contains("UiRegistry");
        await Assert.That(navMenu).Contains("Registry.Navigation");
    }

    [Test]
    public async Task AbstractionAssemblies_DoNotReferenceImplementations()
    {
        string[] abstractionProjects =
        [
            "src/Arrk.Media.Abstractions/Arrk.Media.Abstractions.csproj",
            "src/Arrk.Tv.Abstractions/Arrk.Tv.Abstractions.csproj",
            "src/Arrk.Music.Abstractions/Arrk.Music.Abstractions.csproj",
            "src/Arrk.Downloads.Abstractions/Arrk.Downloads.Abstractions.csproj",
            "src/Arrk.Auth.Abstractions/Arrk.Auth.Abstractions.csproj"
        ];

        List<string> offenders = [];
        foreach (string relative in abstractionProjects)
        {
            string content = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
            foreach (Match match in Regex.Matches(content, @"ProjectReference Include=""([^""]+)\.csproj"""))
            {
                string target = match.Groups[1].Value;
                if (!target.EndsWith(".Abstractions", StringComparison.Ordinal))
                    offenders.Add($"{relative} -> {target}");
            }
        }

        await Assert.That(offenders).IsEmpty();
    }

    [Test]
    public async Task Core_ReferencesOnlyAbstractionsNotFeatureImplementations()
    {
        string content = File.ReadAllText(
            Path.Combine(RepoPaths.Root, "src/Arrk.Core/Arrk.Core.csproj"));

        List<string> offenders = [];
        foreach (Match match in Regex.Matches(content, @"ProjectReference Include=""([^""]+)\.csproj"""))
        {
            string target = match.Groups[1].Value;
            if (!target.EndsWith(".Abstractions", StringComparison.Ordinal))
                offenders.Add(target);
        }

        await Assert.That(offenders).IsEmpty();
    }

}
