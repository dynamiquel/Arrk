using System.Reflection;

namespace Arrk.Ui;

/// <summary>
/// Tells the UI layer which assemblies hold UI: the host assembly plus every loaded module
/// assembly. The host provides this so the router, the region scan and route validation all read
/// one source of truth.
/// </summary>
public interface IUiAssemblyProvider
{
    /// <summary>The host application assembly (routable components and regions in the shell).</summary>
    Assembly HostAssembly { get; }

    /// <summary>Additional assemblies beyond the host (one per module) to scan and route to.</summary>
    IReadOnlyList<Assembly> AdditionalAssemblies { get; }
}
