using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Arrk.Core.Configuration;
using Arrk.Core.Services;

namespace Arrk.Core.Modules;

public interface IArrkModule
{
    string ModuleName { get; }

    /// <summary>
    /// The module's <c>major.minor.revision</c> version. Defaults to the version stamped on the
    /// module's assembly (built-in modules share the app version from
    /// <c>Directory.Build.props</c>); third-party modules may override it.
    /// </summary>
    string ModuleVersion => ArrkVersion.FromAssembly(GetType().Assembly).ToString();

    /// <summary>Module author shown on the About page. Built-in modules are authored by Arrk.</summary>
    string ModuleAuthor => "Arrk";

    /// <summary>One-line description of what the module does, shown on the About page.</summary>
    string ModuleDescription => "";

    IReadOnlyList<Service> GetServices();

    IReadOnlyList<IConfigSectionProvider> GetConfigSections() => [];

    void ConfigureServices(IServiceCollection services, IConfiguration configuration) { }

    void ConfigureHostedServices(IServiceCollection services) { }

    void ConfigureJobs(IArrkJobProvider jobs, IServiceProvider services) { }

    /// <summary>
    /// Maps the module's own HTTP endpoints (playback proxies, art endpoints, ...). The host passes
    /// the endpoint router and calls this once at startup, exactly like <see cref="ConfigureJobs"/>,
    /// so a module never needs the host to know about its routes.
    /// </summary>
    void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

    IReadOnlyList<ModuleDependency> GetDependencies() => [];
}
