using Arrk.Auth.Abstractions;

namespace Arrk.Auth.Services;

/// <summary>
/// Renders the self-service sign-up page. Shared by the open and approval modes; the copy tells
/// the requester whether the account is immediate or needs an administrator.
/// </summary>
public static class RegisterPageHtml
{
    public static string Render(
        RegistrationMode mode,
        string? error,
        string? username,
        string? antiforgeryToken = null)
    {
        string intro = mode == RegistrationMode.Open
            ? "Create an account to start using Arrk."
            : "Create an account. An administrator must approve it before you can sign in.";

        string body = $$"""
            {{AuthPageHtml.Error(error)}}
            <form method="post" action="/account/register">
                <input type="hidden" name="__RequestVerificationToken" value="{{AuthPageHtml.H(antiforgeryToken ?? "")}}" />
                {{AuthPageHtml.Field("username", "Username", "text", username ?? "", "username")}}
                {{AuthPageHtml.Field("password", "Password", "password", "", "new-password")}}
                {{AuthPageHtml.Field("confirmPassword", "Confirm password", "password", "", "new-password")}}
                <button type="submit">Create account</button>
            </form>
            <div class="alt">
                Already have an account? <a href="/login">Sign in</a>
            </div>
            """;

        return AuthPageHtml.Render("Create account", intro, body);
    }
}
