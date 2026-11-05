using System.Reflection;
using Stackarr.Ui;

namespace Stackarr.App.Services.Ui;

/// <summary>
/// Tells the UI layer which assemblies hold UI: the host plus every module the <see cref="ModuleLoader"/>
/// loaded. The router and the region scan both read this, so a module contributes pages and regions
/// without any shell change.
/// </summary>
public sealed class UiAssemblyProvider : IUiAssemblyProvider
{
    public UiAssemblyProvider(ModuleLoader moduleLoader)
    {
        HostAssembly = typeof(UiAssemblyProvider).Assembly;
        AdditionalAssemblies = moduleLoader.Modules
            .Select(module => module.GetType().Assembly)
            .Where(assembly => assembly != HostAssembly)
            .Distinct()
            .ToList();
    }

    public Assembly HostAssembly { get; }

    public IReadOnlyList<Assembly> AdditionalAssemblies { get; }
}
