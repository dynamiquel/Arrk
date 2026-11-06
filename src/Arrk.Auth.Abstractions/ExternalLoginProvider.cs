using Microsoft.Extensions.Options;

namespace Arrk.Auth.Abstractions;

/// <summary>An upstream sign-in provider shown on the login page.</summary>
public sealed record LoginProvider(string Scheme, string Label);

/// <summary>
/// A module-owned sign-in provider. The module owns the OAuth/OIDC flow; the shell only needs to
/// know how to advertise it and where to start it. Keeping this in Core lets a module add a login
/// method without the host referencing the module's implementation.
/// </summary>
public interface IExternalLoginProvider
{
    string Scheme { get; }

    string DisplayName { get; }

    /// <summary>Whether the provider is configured and enabled right now.</summary>
    bool IsEnabled { get; }

    /// <summary>Whether the provider should appear on the sign-in page (linking is separate).</summary>
    bool ShowOnLogin => true;

    /// <summary>
    /// Builds the URL that starts the flow. <paramref name="mode"/> is <c>login</c> or <c>link</c>;
    /// linking carries the Arrk user id so the callback can bind the identity to that account.
    /// </summary>
    string BuildStartUrl(string mode, int? linkUserId, string? returnUrl);
}
