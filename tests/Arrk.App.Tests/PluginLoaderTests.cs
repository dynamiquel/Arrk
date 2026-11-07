using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Arrk.App;
using Arrk.Core;
using Arrk.Core.Documentation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Arrk.TestSupport;

namespace Arrk.App.Tests;

/// <summary>
/// Exercises the drop-in plugin loader: a missing or asset-only plugins folder loads nothing, and a
/// real plugin assembly dropped in the folder has its <c>IArrkModule</c> registered with the
/// loader (the same wiring the host runs before building its container).
/// </summary>
public class PluginLoaderTests
{
    static ModuleLoader NewLoader() => new(NullLogger<ModuleLoader>.Instance);

    [Test]
    public async Task MissingDirectory_LoadsNothing()
    {
        ModuleLoader loader = NewLoader();

        IReadOnlyList<System.Reflection.Assembly> loaded =
            loader.LoadPlugins(Path.Combine(Path.GetTempPath(), $"stackarr-missing-{Guid.NewGuid():N}"));

        await Assert.That(loaded).IsEmpty();
        await Assert.That(loader.Modules).IsEmpty();
    }

    [Test]
    public async Task NonAssemblyFiles_AreIgnored()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"stackarr-plugins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "readme.txt"), "not a plugin");
            await File.WriteAllTextAsync(Path.Combine(dir, "garbage.dll"), "still not a plugin");

            ModuleLoader loader = NewLoader();
            IReadOnlyList<System.Reflection.Assembly> loaded = loader.LoadPlugins(dir);

            await Assert.That(loaded).IsEmpty();
            await Assert.That(loader.Modules).IsEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task PluginAssembly_RegistersItsModule()
    {
        string? pluginDll = FindSamplePluginDll();
        if (pluginDll is null)
        {
            // The sample plugin has not been built in this configuration; nothing to load.
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), $"stackarr-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(pluginDll, Path.Combine(dir, Path.GetFileName(pluginDll)));

            ModuleLoader loader = NewLoader();
            IReadOnlyList<System.Reflection.Assembly> loaded = loader.LoadPlugins(dir);

            await Assert.That(loaded.Count).IsEqualTo(1);
            await Assert.That(loader.Modules.Select(m => m.ModuleName)).Contains("SamplePlugin");

            ServiceCollection services = new();
            services.AddArrkCore();
            loader.ConfigureAllServices(services, new ConfigurationBuilder().Build());
            await using ServiceProvider provider = services.BuildServiceProvider();
            DocumentationCatalog documents = provider.GetRequiredService<DocumentationCatalog>();
            await Assert.That(documents.Topics.Single().Id).IsEqualTo("sampleplugin/start");
            await Assert.That(documents.GetDocument("sampleplugin/start")!.Markdown).Contains("does not require AI");
            await Assert.That(documents.Search("sample page").Count).IsGreaterThan(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    static string? FindSamplePluginDll()
    {
        string samplesRoot = Path.Combine(RepoPaths.Root, "samples", "Arrk.SamplePlugin");
        if (!Directory.Exists(samplesRoot))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(samplesRoot, "Arrk.SamplePlugin.dll", SearchOption.AllDirectories)
            .Where(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
