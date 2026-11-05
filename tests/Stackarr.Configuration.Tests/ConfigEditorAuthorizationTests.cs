using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Stackarr.Configuration.Components.Pages;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Configuration.Tests;

/// <summary>
/// <c>/config</c> renders the live YAML, including provider keys, VPN credentials and tunnel
/// tokens. It must never be reachable by a normal account.
/// </summary>
public class ConfigEditorAuthorizationTests
{
    [Test]
    public async Task ConfigEditor_RequiresAnAdministrator()
    {
        AuthorizeAttribute? authorize = typeof(ConfigEditor).GetCustomAttribute<AuthorizeAttribute>();

        await Assert.That(authorize).IsNotNull();
        await Assert.That(authorize!.Policy).IsEqualTo(StackarrPolicies.AdminOnly);
    }
}
