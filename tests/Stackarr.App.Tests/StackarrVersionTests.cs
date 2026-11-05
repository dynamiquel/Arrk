using Microsoft.Extensions.Logging.Abstractions;
using Stackarr.App;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;

namespace Stackarr.App.Tests;

public class StackarrVersionTests
{
    /// <summary>
    /// The version stamped on every assembly, read from <c>Directory.Build.props</c> so a release
    /// bump never needs this test edited.
    /// </summary>
    static string ConfiguredVersion
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                dir = dir.Parent;
            string props = File.ReadAllText(Path.Combine(
                dir?.FullName ?? throw new InvalidOperationException("Could not locate Directory.Build.props"),
                "Directory.Build.props"));
            System.Text.RegularExpressions.Match match =
                System.Text.RegularExpressions.Regex.Match(props, @"<Version>([^<]+)</Version>");
            return match.Groups[1].Value.Trim();
        }
    }

    [Test]
    public async Task Parse_ReadsMajorMinorRevision()
    {
        StackarrVersion version = StackarrVersion.Parse("0.1.0");

        await Assert.That(version.Major).IsEqualTo(0);
        await Assert.That(version.Minor).IsEqualTo(1);
        await Assert.That(version.Revision).IsEqualTo(0);
        await Assert.That(version.ToString()).IsEqualTo("0.1.0");
    }

    [Test]
    public async Task TryParse_IgnoresBuildMetadataAndPrerelease()
    {
        await Assert.That(StackarrVersion.TryParse("0.1.0+abc123", out StackarrVersion build)).IsTrue();
        await Assert.That(build.ToString()).IsEqualTo("0.1.0");

        await Assert.That(StackarrVersion.TryParse("0.1.0-beta.1", out StackarrVersion pre)).IsTrue();
        await Assert.That(pre.ToString()).IsEqualTo("0.1.0");
    }

    [Test]
    public async Task TryParse_TreatsMissingRevisionAsZero()
    {
        await Assert.That(StackarrVersion.TryParse("0.1", out StackarrVersion version)).IsTrue();
        await Assert.That(version.ToString()).IsEqualTo("0.1.0");
    }

    [Test]
    public async Task TryParse_RejectsMalformedInput()
    {
        await Assert.That(StackarrVersion.TryParse("", out _)).IsFalse();
        await Assert.That(StackarrVersion.TryParse((string?)null, out _)).IsFalse();
        await Assert.That(StackarrVersion.TryParse("abc", out _)).IsFalse();
        await Assert.That(StackarrVersion.TryParse("1.2.3.4", out _)).IsFalse();
    }

    [Test]
    public async Task Current_RejectsConfiguredAppVersion()
    {
        await Assert.That(StackarrVersion.Current.ToString()).IsEqualTo(ConfiguredVersion);
    }

    [Test]
    public async Task ModuleVersion_DefaultsToAssemblyVersion()
    {
        IStackarrModule module = new TraefikModule();
        string moduleVersion = module.ModuleVersion;

        await Assert.That(StackarrVersion.TryParse(moduleVersion, out StackarrVersion version)).IsTrue();
        await Assert.That(version.ToString()).IsEqualTo(ConfiguredVersion);
    }

    [Test]
    public async Task GetModules_ReportsNameAndVersionForEveryModule()
    {
        ModuleLoader loader = new(NullLogger<ModuleLoader>.Instance);
        loader.RegisterModule(new TraefikModule());

        IReadOnlyList<ModuleInfo> modules = loader.GetModules();

        await Assert.That(modules.Count).IsEqualTo(1);
        await Assert.That(modules[0].ModuleName).IsEqualTo("ReverseProxy");
        await Assert.That(StackarrVersion.TryParse(modules[0].ModuleVersion, out _)).IsTrue();
    }
}
