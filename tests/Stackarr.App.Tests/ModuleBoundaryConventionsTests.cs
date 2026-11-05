using System.IO;
using System.Text.RegularExpressions;
using Stackarr.TestSupport;

namespace Stackarr.App.Tests;

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
        string appRoot = Path.Combine(RepoPaths.Root, "src/Stackarr.App");
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
            "src/Stackarr.Music/Components/Pages/Music.razor",
            "src/Stackarr.Tvarr/Components/Pages/Movies.razor",
            "src/Stackarr.Tvarr/Components/Pages/TvShows.razor"
        ];

        foreach (string relative in gated)
        {
            string content = File.ReadAllText(Path.Combine(RepoPaths.Root, relative));
            await Assert.That(content).Contains("UiRegistry");
            await Assert.That(content).Contains("IsAvailable(");
        }

        string discover = File.ReadAllText(Path.Combine(RepoPaths.Root, "src/Stackarr.App/Components/Pages/Discover.razor"));
        await Assert.That(discover).Contains("UiRegistry");
        await Assert.That(discover).Contains("DiscoverSources");
    }

    [Test]
    public async Task DiscoverPage_DoesNotReferenceModuleUi()
    {
        // The shell's Discover page is composed from registered IDiscoverSources; it must not know
        // about a specific module's pages or catalog types.
        string discover = File.ReadAllText(
            Path.Combine(RepoPaths.Root, "src/Stackarr.App/Components/Pages/Discover.razor"));

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
            "src/Stackarr.App/Services/Ui/AppNavigationProvider.cs",
            "src/Stackarr.App/Services/Ui/AppHeadAssetProvider.cs",
            "src/Stackarr.App/Components/Layout/NavMenu.razor"
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
            Path.Combine(RepoPaths.Root, "src/Stackarr.App/Components/Layout/NavMenu.razor"));

        await Assert.That(navMenu).Contains("UiRegistry");
        await Assert.That(navMenu).Contains("Registry.Navigation");
    }

    [Test]
    public async Task AbstractionAssemblies_DoNotReferenceImplementations()
    {
        string[] abstractionProjects =
        [
            "src/Stackarr.Media.Abstractions/Stackarr.Media.Abstractions.csproj",
            "src/Stackarr.Tv.Abstractions/Stackarr.Tv.Abstractions.csproj",
            "src/Stackarr.Music.Abstractions/Stackarr.Music.Abstractions.csproj",
            "src/Stackarr.Downloads.Abstractions/Stackarr.Downloads.Abstractions.csproj",
            "src/Stackarr.Auth.Abstractions/Stackarr.Auth.Abstractions.csproj"
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
            Path.Combine(RepoPaths.Root, "src/Stackarr.Core/Stackarr.Core.csproj"));

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
