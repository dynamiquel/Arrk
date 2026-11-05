using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Stackarr.Core.Configuration;
using Stackarr.Core.Modules;
using Stackarr.Core.Services;

namespace Stackarr.Subtitles.OpenSubtitles;

/// <summary>
/// The built-in OpenSubtitles provider. It runs in-process and contributes only an
/// <see cref="ISubtitleProvider"/>; the shared dispatcher owns status and automatic downloads.
/// </summary>
public class OpenSubtitlesModule : IStackarrModule
{
    public const string EnabledConfigPath = "services:subtitles:opensubtitles";

    public string ModuleName => "Subtitles - OpenSubtitles";
    public string ModuleDescription => "Built-in OpenSubtitles subtitle provider";

    public IReadOnlyList<Service> GetServices() => [];

    public IReadOnlyList<IConfigSectionProvider> GetConfigSections() =>
        [new ConfigSectionProvider<OpenSubtitlesConfig>(EnabledConfigPath, "OpenSubtitles",
            icon: "Subtitles", group: "Services", order: 51, groupOrder: 20)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureConfig<OpenSubtitlesConfig>(configuration.GetSection(EnabledConfigPath));

        services.AddSingleton(sp => new OpenSubtitlesClient(
            sp.GetRequiredService<IOptionsMonitor<OpenSubtitlesConfig>>()));
        services.AddSingleton<OpenSubtitlesSubtitleProvider>();
        services.AddSingleton<ISubtitleProvider>(sp => sp.GetRequiredService<OpenSubtitlesSubtitleProvider>());
    }
}
