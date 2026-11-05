using System.Text;
using Stackarr.Auth.Abstractions;

namespace Stackarr.Auth.Services;

/// <summary>
/// Renders the standalone sign-in page. Served by a minimal API endpoint (not Blazor) so the
/// form and OIDC redirects work before an interactive circuit exists.
/// </summary>
public static class LoginPageHtml
{
    public static string Render(
        string? error,
        string? notice,
        string? username,
        string? returnUrl,
        bool allowPasswordLogin,
        bool allowRegistration,
        IReadOnlyList<LoginProvider> providers,
        string? antiforgeryToken = null)
    {
        StringBuilder providerBlock = new();
        foreach (LoginProvider provider in providers)
        {
            providerBlock.Append(
                $"""<a class="sso" href="/login/{AuthPageHtml.H(provider.Scheme)}?returnUrl={AuthPageHtml.H(returnUrl ?? "/")}">{AuthPageHtml.H(provider.Label)}</a>""");
        }

        string passwordBlock = allowPasswordLogin
            ? $$"""
                <form method="post" action="/account/login">
                    <input type="hidden" name="returnUrl" value="{{AuthPageHtml.H(returnUrl ?? "/")}}" />
                    <input type="hidden" name="__RequestVerificationToken" value="{{AuthPageHtml.H(antiforgeryToken ?? "")}}" />
                    {{AuthPageHtml.Field("username", "Username", "text", username ?? "", "username")}}
                    {{AuthPageHtml.Field("password", "Password", "password", "", "current-password")}}
                    <button type="submit">Sign in</button>
                </form>
                """
            : "";

        string divider = (allowPasswordLogin && providers.Count > 0)
            ? """<div class="divider"><span>or</span></div>"""
            : "";

        if (!allowPasswordLogin && providers.Count == 0)
            passwordBlock = AuthPageHtml.Error("No sign-in method is configured. Enable an identity provider or password login.");

        string registration = allowRegistration
            ? $"""
                <div class="alt">
                    New here? <a href="/register">Create an account</a>
                </div>
                """
            : "";

        string body = $$"""
            {{AuthPageHtml.Error(error)}}
            {{AuthPageHtml.Notice(notice)}}
            {{passwordBlock}}
            {{divider}}
            {{providerBlock}}
            {{registration}}
            """;

        return AuthPageHtml.Render("Sign in", "Sign in to manage your media stack.", body);
    }
}
