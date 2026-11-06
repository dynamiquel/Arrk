using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Arrk.App;
using Arrk.App.Services.Ui;
using Arrk.Auth.Abstractions;

namespace Arrk.Auth.Tests;

/// <summary>
/// Structural guard for the UI: every routable page must require authentication, and the
/// administrative pages must additionally require the admin role. This catches a page that is
/// added without an authorization attribute (which would otherwise be exposed to standard users).
/// <para>
/// Pages now live in the host <b>and</b> in each module's Razor class library, so the scan mirrors
/// production: it enumerates the same host + module assemblies the router is given.
/// </para>
/// </summary>
public class PageAuthorizationTests
{
    static IEnumerable<Assembly> UiAssemblies()
    {
        ModuleLoader loader = ServiceRegistryFactory.CreateModuleLoader();
        UiAssemblyProvider provider = new(loader);
        yield return provider.HostAssembly;
        foreach (Assembly assembly in provider.AdditionalAssemblies)
        {
            yield return assembly;
        }
    }

    static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    static IEnumerable<(string Route, Type Type)> RoutablePages()
    {
        foreach (Type type in UiAssemblies().SelectMany(SafeTypes))
        {
            if (type.IsAbstract || type.IsInterface)
                continue;

            foreach (RouteAttribute route in type.GetCustomAttributes<RouteAttribute>(inherit: true))
                yield return (route.Template, type);
        }
    }

    static AuthorizeAttribute? Authorize(Type type)
        => type.GetCustomAttributes<AuthorizeAttribute>(inherit: true).FirstOrDefault();

    [Test]
    public async Task AdminPages_RequireAdminRole()
    {
        string[] adminRoutes =
        [
            "/docker", "/config", "/logs", "/scheduler", "/users", "/containers",
            "/docker/container/{Id}", "/docker/volume/{Name}", "/docker/network/{Id}"
        ];

        List<(string Route, Type Type)> pages = RoutablePages().ToList();
        foreach (string route in adminRoutes)
        {
            (string Route, Type Type) page = pages.SingleOrDefault(p => p.Route == route);
            await Assert.That(page.Type).IsNotNull();

            AuthorizeAttribute? authorize = Authorize(page.Type);
            await Assert.That(authorize).IsNotNull();
            await Assert.That(authorize!.Policy).IsEqualTo(ArrkPolicies.AdminOnly);
        }
    }

    [Test]
    public async Task UserPages_RequireAuthentication()
    {
        string[] userRoutes =
        [
            "/", "/movies", "/tv", "/music", "/downloads", "/events", "/info",
            "/discover", "/discover/{Source}", "/account"
        ];

        List<(string Route, Type Type)> pages = RoutablePages().ToList();
        foreach (string route in userRoutes)
        {
            (string Route, Type Type) page = pages.SingleOrDefault(p => p.Route == route);
            await Assert.That(page.Type).IsNotNull();

            AuthorizeAttribute? authorize = Authorize(page.Type);
            await Assert.That(authorize).IsNotNull();
            await Assert.That(string.IsNullOrEmpty(authorize!.Policy)).IsTrue();
        }
    }

    [Test]
    public async Task DeniedPage_IsAnonymous()
    {
        (string Route, Type Type) page = RoutablePages().Single(p => p.Route == "/denied");
        await Assert.That(page.Type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any()).IsTrue();
    }

    [Test]
    public async Task EveryRoutablePage_HasAnAuthorizationAttribute()
    {
        List<string> unprotected = [];

        foreach ((string route, Type type) in RoutablePages())
        {
            bool hasAuthorize = Authorize(type) is not null;
            bool allowAnonymous = type.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true).Any();
            if (!hasAuthorize && !allowAnonymous)
                unprotected.Add($"{route} ({type.Name})");
        }

        await Assert.That(unprotected).IsEmpty();
    }
}
