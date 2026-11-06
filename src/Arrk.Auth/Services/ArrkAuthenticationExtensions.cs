using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Arrk.Auth.Abstractions;

namespace Arrk.Auth.Services;

/// <summary>
/// Wires Arrk's authentication: a cookie session for the UI, ASP.NET Core Identity for local
/// credentials, and pluggable upstream OIDC providers (Discord first) that federate into it.
/// </summary>
public static class ArrkAuthenticationExtensions
{
    public const string DiscordScheme = ExternalLoginSchemes.Discord;

    /// <summary>AuthenticationProperties key tracking whether the current Discord attempt is a silent probe.</summary>
    const string PromptPropertiesKey = "arrk:oidc_prompt";
    /// <summary>AuthenticationProperties key marking an OIDC challenge as an account-link flow.</summary>
    const string LinkPropertiesKey = "arrk:oidc_link";
    const string SilentPrompt = "none";

    public static IServiceCollection AddArrkAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        AuthConfig auth = configuration.GetSection(AuthConfig.SectionName).Get<AuthConfig>() ?? new();

        services.AddHttpContextAccessor();
        services.AddCascadingAuthenticationState();

        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "arrk.antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = auth.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.HeaderName = "X-CSRF-TOKEN";
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(ArrkPolicies.AuthRateLimit, http =>
            {
                AuthConfig current = http.RequestServices
                    .GetRequiredService<IOptionsMonitor<AuthConfig>>().CurrentValue;
                int permits = Math.Clamp(current.AuthRateLimitPerMinute, 0, 10000);
                string key = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits == 0 ? int.MaxValue : permits,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

        AuthenticationBuilder builder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        })
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = "arrk.auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = auth.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/denied";
            options.ExpireTimeSpan = TimeSpan.FromDays(Math.Clamp(auth.SessionDays, 1, 365));
            options.SlidingExpiration = true;
            options.Events = new CookieAuthenticationEvents
            {
                // A cookie is only a hint: every request re-checks the account so disabling,
                // demoting or deleting a user revokes access immediately instead of waiting for
                // the cookie to expire. The security stamp also invalidates sessions after a
                // password change or reset.
                OnValidatePrincipal = ValidatePrincipalAsync
            };
        });

        // Rebuild the cookie options when the configuration reloads so auth.require_https and
        // auth.session_days take effect live (the handlers re-read options per request). The source
        // is named for the cookie scheme so the monitor invalidates that exact options entry.
        services.AddSingleton<IOptionsChangeTokenSource<CookieAuthenticationOptions>>(
            new ConfigurationChangeTokenSource<CookieAuthenticationOptions>(
                CookieAuthenticationDefaults.AuthenticationScheme, configuration));
        services.AddSingleton<IConfigureOptions<CookieAuthenticationOptions>, AuthCookieConfigurator>();

        // Register the upstream OIDC handler unconditionally so enabling Discord (and filling in the
        // client id) in the config editor takes effect without an app restart. The handler is only
        // ever challenged when LoginProviders treats it as enabled, so a disabled provider is inert.
        DiscordConfig discord = auth.External.Discord;
        builder.AddOpenIdConnect(DiscordScheme, discord.DisplayName, options =>
        {
            options.Authority = string.IsNullOrWhiteSpace(discord.Authority) ? "https://discord.com" : discord.Authority;
            // A non-empty placeholder keeps the handler valid when Discord is disabled; the live
            // configurator below replaces it with the configured value when one is set.
            options.ClientId = string.IsNullOrWhiteSpace(discord.ClientId) ? "arrk-discord" : discord.ClientId;
            options.ClientSecret = discord.ClientSecret ?? "";
            options.ResponseType = OpenIdConnectResponseType.Code;
            options.UsePkce = true;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.CallbackPath = string.IsNullOrWhiteSpace(discord.CallbackPath) ? "/signin-discord" : discord.CallbackPath;
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.Scope.Clear();
            options.Scope.Add("openid");
            // Discord's identify scope provides preferred_username, nickname and picture. The email
            // scope is deliberately not requested: an upstream email is optional profile data, never
            // a login requirement or a username.
            options.Scope.Add("identify");
            options.Events = new OpenIdConnectEvents
            {
                OnRedirectToIdentityProvider = context =>
                {
                    // Probe silently first. Discord only skips its authorisation screen when the
                    // request carries prompt=none and the user is still signed in to Discord and
                    // has already authorised Arrk; otherwise it re-prompts on every login.
                    if (!context.Properties.Items.ContainsKey(PromptPropertiesKey))
                        context.Properties.Items[PromptPropertiesKey] = SilentPrompt;

                    if (string.Equals(context.Properties.Items[PromptPropertiesKey], SilentPrompt, StringComparison.Ordinal))
                        context.ProtocolMessage.Prompt = SilentPrompt;

                    return Task.CompletedTask;
                },
                OnRemoteFailure = async context =>
                {
                    // A silent probe fails (login_required / consent_required) when the user has
                    // no Discord session or has not granted consent yet. Fall back to an
                    // interactive prompt exactly once; the retry clears the silent marker.
                    bool silent = context.Properties?.Items.TryGetValue(PromptPropertiesKey, out string? prompt) == true
                        && prompt == SilentPrompt;

                    context.HandleResponse();

                    if (silent)
                    {
                        AuthenticationProperties retry = new()
                        {
                            RedirectUri = context.Properties?.RedirectUri ?? "/"
                        };
                        retry.Items[PromptPropertiesKey] = "interactive";
                        // Keep an account-link flow an account-link flow across the retry.
                        if (context.Properties?.Items.TryGetValue(LinkPropertiesKey, out string? linkOwner) == true)
                            retry.Items[LinkPropertiesKey] = linkOwner;

                        await context.HttpContext.ChallengeAsync(DiscordScheme, retry);
                        return;
                    }

                    bool linking = context.Properties?.Items.ContainsKey(LinkPropertiesKey) == true;
                    context.Response.Redirect(linking ? "/account?linkError=failed" : "/login?error=oidc");
                },
                OnTicketReceived = async context =>
                {
                    ExternalIdentityResolver resolver =
                        context.HttpContext.RequestServices.GetRequiredService<ExternalIdentityResolver>();

                    // An authenticated user can attach Discord to their existing Arrk account.
                    // This never signs in as a different account: it only adds the external login.
                    if (context.Properties?.Items.TryGetValue(LinkPropertiesKey, out string? linkOwner) == true
                        && int.TryParse(linkOwner, out int linkUserId))
                    {
                        context.HandleResponse();

                        int? signedInUserId = int.TryParse(
                            context.HttpContext.User.FindFirst(ArrkClaims.UserIdClaim)?.Value,
                            out int currentUserId)
                            ? currentUserId
                            : null;

                        if (signedInUserId is null || signedInUserId != linkUserId)
                        {
                            context.Response.Redirect("/login");
                            return;
                        }

                        bool linked = await resolver.LinkAsync(
                            linkUserId, DiscordScheme, context.Principal!, context.HttpContext.RequestAborted);
                        context.Response.Redirect(linked ? "/account?linked=discord" : "/account?linkError=taken");
                        return;
                    }

                    ExternalSignInResult result = await resolver.SignInAsync(
                        DiscordScheme, context.Principal!, context.HttpContext.RequestAborted);

                    if (result.Principal is null)
                    {
                        // Pending sign-ups are real accounts but get no session; unknown identities
                        // are refused outside Open mode without revealing whether an account exists.
                        context.HandleResponse();
                        string error = result.Status switch
                        {
                            ExternalSignInStatus.Pending => "pending",
                            ExternalSignInStatus.Disabled => "disabled",
                            _ => "discord-unlinked"
                        };
                        context.Response.Redirect($"/login?error={error}");
                        return;
                    }

                    context.Principal = result.Principal;

                    // Issue a persistent cookie like password sign-in. The OIDC handler otherwise
                    // writes a browser-session cookie that expires when the browser closes.
                    int sessionDays = context.HttpContext.RequestServices
                        .GetRequiredService<IOptionsMonitor<AuthConfig>>().CurrentValue.SessionDays;
                    AuthenticationProperties properties = context.Properties ?? new();
                    properties.IsPersistent = true;
                    properties.AllowRefresh = true;
                    properties.ExpiresUtc = DateTimeOffset.UtcNow.AddDays(Math.Clamp(sessionDays, 1, 365));
                    context.Properties = properties;
                }
            };
        });

        services.AddAuthorization(options =>
        {
            // Every ordinary page/endpoint requires a real, approved account. Pending sign-ups are
            // authenticated identities but belong to no role that any policy accepts.
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .RequireRole(ArrkRoles.User, ArrkRoles.Admin)
                .Build();

            options.AddPolicy(ArrkPolicies.Authenticated, policy => policy
                .RequireAuthenticatedUser()
                .RequireRole(ArrkRoles.User, ArrkRoles.Admin));
            options.AddPolicy(ArrkPolicies.AdminOnly, policy => policy.RequireRole(ArrkRoles.Admin));
        });

        // Keep the upstream OIDC options in step with the live auth config so enabling Discord and
        // filling in its client id/secret in the config editor applies without a restart.
        services.AddSingleton<IOptionsChangeTokenSource<OpenIdConnectOptions>>(
            new ConfigurationChangeTokenSource<OpenIdConnectOptions>(DiscordScheme, configuration));
        services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, DiscordOidcConfigurator>();

        services.AddSingleton<LoginProviderRegistry>();
        services.AddScoped<ExternalIdentityResolver>();

        return services;
    }

    /// <summary>
    /// Re-validates an existing cookie against the live account row. Runs on every authenticated
    /// request, so a disabled or demoted account loses access without waiting for the cookie to
    /// expire. A transient database failure is treated as "cannot check" rather than logging every
    /// user out; the next request validates normally.
    /// </summary>
    static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        ClaimsPrincipal? principal = context.Principal;
        if (principal is null)
        {
            await RejectAsync(context);
            return;
        }

        string? idValue = principal.FindFirst(ArrkClaims.UserIdClaim)?.Value;
        if (!int.TryParse(idValue, out int userId))
        {
            await RejectAsync(context);
            return;
        }

        ArrkSessionState? state;
        try
        {
            UserService users = context.HttpContext.RequestServices.GetRequiredService<UserService>();
            state = await users.GetSessionStateAsync(userId);
        }
        catch
        {
            return;
        }

        bool approved = state is not null
            && state.IsActive
            && state.Role != UserRole.Pending
            && (state.Roles.Contains(ArrkRoles.User) || state.Roles.Contains(ArrkRoles.Admin));

        if (!approved)
        {
            await RejectAsync(context);
            return;
        }

        string? stamp = principal.FindFirst(ArrkClaims.SecurityStampClaim)?.Value;
        if (string.IsNullOrEmpty(stamp)
            || !string.Equals(stamp, state!.SecurityStamp, StringComparison.Ordinal))
        {
            await RejectAsync(context);
            return;
        }

        string provider = principal.FindFirst(ArrkClaims.ProviderClaim)?.Value ?? "local";
        bool nameMatches = string.Equals(principal.Identity?.Name, state.User.Username, StringComparison.Ordinal);
        List<string> cookieRoles = [.. principal.FindAll(ClaimTypes.Role).Select(c => c.Value)];
        bool rolesMatch = cookieRoles.Count == state.Roles.Count
            && state.Roles.All(role => cookieRoles.Contains(role, StringComparer.Ordinal));

        if (nameMatches && rolesMatch)
            return;

        // A rename or role change is reflected live. The stamp is unchanged, so the user keeps
        // their session while the principal catches up.
        ClaimsIdentity identity = ArrkClaims.Create(
            state.User,
            state.Roles,
            CookieAuthenticationDefaults.AuthenticationScheme,
            provider,
            state.SecurityStamp);
        context.ReplacePrincipal(new ClaimsPrincipal(identity));
        context.ShouldRenew = true;
    }

    static async Task RejectAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        try
        {
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
        catch
        {
            // The rejected principal is already unauthenticated; a sign-out failure must not turn
            // a planned 401/redirect into a 500.
        }
    }

    /// <summary>Anonymous sign-in/sign-out endpoints. Plain HTTP so they work before a circuit exists.</summary>
    public static void MapArrkAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/login", (HttpContext http, IOptionsMonitor<AuthConfig> auth, RegistrationService registration, LoginProviderRegistry providers, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            if (http.User.Identity?.IsAuthenticated == true)
                return Results.Redirect("/");

            AuthConfig config = auth.CurrentValue;
            string error = http.Request.Query["error"].ToString() switch
            {
                "1" => "Invalid username or password.",
                "oidc" => "Sign-in with Discord failed. Please try again.",
                "pending" => "This account is waiting for administrator approval. You can sign in after it has been approved.",
                "disabled" => "This account has been disabled.",
                "discord-unlinked" => "That Discord account isn't linked to an Arrk account. Sign in with your Arrk username and password, then link Discord from My account.",
                "mal" => "Sign-in with MyAnimeList failed. Please try again.",
                "mal-state" => "That MyAnimeList sign-in link has expired. Start again.",
                "mal-taken" => "That MyAnimeList account is already linked to another Arrk user.",
                "mal-unlinked" => "That MyAnimeList account isn't linked to an Arrk account. Sign in with Arrk, then link MyAnimeList from My account.",
                _ => ""
            };
            string notice = http.Request.Query["notice"].ToString() switch
            {
                "pending" => "Account created. An administrator must approve it before you can sign in.",
                _ => ""
            };
            string? username = http.Request.Query["username"];

            // The cookie handler's challenge uses "ReturnUrl" (capital R); our own links use
            // "returnUrl". Accept both so an interrupted OIDC/SSO flow resumes after sign-in.
            string? requested = http.Request.Query["returnUrl"];
            if (string.IsNullOrEmpty(requested))
                requested = http.Request.Query["ReturnUrl"];
            string returnUrl = NormalizeReturnUrl(requested);
            string? csrf = config.AllowPasswordLogin
                ? antiforgery.GetAndStoreTokens(http).RequestToken
                : null;
            return Results.Content(
                LoginPageHtml.Render(
                    error,
                    notice,
                    username,
                    returnUrl,
                    config.AllowPasswordLogin,
                    registration.IsSelfServiceEnabled,
                    providers.Login,
                    csrf),
                "text/html");
        }).AllowAnonymous();

        endpoints.MapGet("/register", (HttpContext http, IOptionsMonitor<AuthConfig> auth, RegistrationService registration, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            if (http.User.Identity?.IsAuthenticated == true)
                return Results.Redirect("/");

            if (!registration.IsSelfServiceEnabled)
                return Results.Redirect("/login");

            string error = http.Request.Query["error"].ToString() switch
            {
                "taken" => "That username is already taken.",
                "invalid" => "Check the username and password, then try again.",
                _ => ""
            };

            return Results.Content(
                RegisterPageHtml.Render(
                    auth.CurrentValue.RegistrationMode,
                    error,
                    http.Request.Query["username"],
                    antiforgery.GetAndStoreTokens(http).RequestToken),
                "text/html");
        }).AllowAnonymous();

        endpoints.MapPost("/account/register", async (
            HttpContext http,
            RegistrationService registration,
            UserService users,
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            if (!registration.IsSelfServiceEnabled)
                return Results.Redirect("/login");

            if (!await IsAntiforgeryValidAsync(http, antiforgery))
                return Results.Redirect("/register?error=invalid");

            IFormCollection form = await http.Request.ReadFormAsync(http.RequestAborted);
            string username = form["username"].ToString();
            string password = form["password"].ToString();
            string confirm = form["confirmPassword"].ToString();

            string failed(string code) =>
                $"/register?error={code}&username={Uri.EscapeDataString(username)}";

            if (!string.Equals(password, confirm, StringComparison.Ordinal))
                return Results.Redirect(failed("invalid"));

            RegistrationOutcome outcome;
            try
            {
                outcome = await registration.RegisterAsync(username, password, http.RequestAborted);
            }
            catch (ArgumentException)
            {
                return Results.Redirect(failed("invalid"));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already taken", StringComparison.Ordinal))
            {
                return Results.Redirect(failed("taken"));
            }
            catch (InvalidOperationException)
            {
                return Results.Redirect(failed("invalid"));
            }

            if (outcome.RequiresApproval)
                return Results.Redirect("/login?notice=pending");

            await SignInAsync(http, users, outcome.User);
            return Results.Redirect("/");
        }).AllowAnonymous().RequireRateLimiting(ArrkPolicies.AuthRateLimit);

        endpoints.MapGet("/account/link/{provider}", (string provider, HttpContext http, LoginProviderRegistry providers) =>
        {
            if (!providers.IsEnabled(provider))
                return Results.Redirect("/account");

            int? userId = int.TryParse(http.User.FindFirst(ArrkClaims.UserIdClaim)?.Value, out int id)
                ? id
                : null;
            if (userId is null)
                return Results.Redirect("/login");

            if (string.Equals(provider, LoginProviderRegistry.DiscordScheme, StringComparison.Ordinal))
            {
                AuthenticationProperties properties = new() { RedirectUri = "/account" };
                // Bind the link to the account that started it. The properties round-trip inside the
                // protected OIDC state, so the callback cannot attach the identity elsewhere.
                properties.Items[LinkPropertiesKey] = userId.Value.ToString();
                return Results.Challenge(properties, [provider]);
            }

            IExternalLoginProvider? external = providers.Find(provider);
            return external is null
                ? Results.Redirect("/account")
                : Results.Redirect(external.BuildStartUrl("link", userId, "/account"));
        }).RequireAuthorization(ArrkPolicies.Authenticated);

        endpoints.MapGet("/login/{provider}", (string provider, HttpContext http, LoginProviderRegistry providers) =>
        {
            if (!providers.Login.Any(p => string.Equals(p.Scheme, provider, StringComparison.Ordinal)))
                return Results.Redirect("/login");

            string returnUrl = NormalizeReturnUrl(http.Request.Query["returnUrl"]);
            if (string.Equals(provider, LoginProviderRegistry.DiscordScheme, StringComparison.Ordinal))
                return Results.Challenge(new AuthenticationProperties { RedirectUri = returnUrl }, [provider]);

            IExternalLoginProvider? external = providers.Find(provider);
            return external is null
                ? Results.Redirect("/login")
                : Results.Redirect(external.BuildStartUrl("login", null, returnUrl));
        }).AllowAnonymous().RequireRateLimiting(ArrkPolicies.AuthRateLimit);

        endpoints.MapPost("/account/login", async (
            HttpContext http,
            UserService users,
            IOptionsMonitor<AuthConfig> auth,
            Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery) =>
        {
            if (!auth.CurrentValue.AllowPasswordLogin)
                return Results.Redirect("/login");

            if (!await IsAntiforgeryValidAsync(http, antiforgery))
                return Results.Redirect("/login?error=1");

            IFormCollection form = await http.Request.ReadFormAsync(http.RequestAborted);
            string username = form["username"].ToString();
            string password = form["password"].ToString();
            string returnUrl = NormalizeReturnUrl(form["returnUrl"]);

            ArrkUser? user = await users.AuthenticateAsync(username, password, http.RequestAborted);
            if (user is null)
                return Results.Redirect($"/login?error=1&username={Uri.EscapeDataString(username)}&returnUrl={Uri.EscapeDataString(returnUrl)}");

            // Pending sign-ups hold valid credentials but must not receive a session.
            if (user.Role == UserRole.Pending)
                return Results.Redirect("/login?error=pending");

            await SignInAsync(http, users, user);
            return Results.Redirect(returnUrl);
        }).AllowAnonymous().RequireRateLimiting(ArrkPolicies.AuthRateLimit);

        endpoints.MapPost("/account/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).AllowAnonymous().DisableAntiforgery();

        endpoints.MapGet("/account/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        }).AllowAnonymous();

        endpoints.MapGet("/account/refresh", async (HttpContext http, UserService users, IOptionsMonitor<AuthConfig> auth) =>
        {
            int id = int.Parse(http.User.FindFirst(ArrkClaims.UserIdClaim)!.Value);
            ArrkUser? user = await users.GetByIdAsync(id, http.RequestAborted);
            if (user is null || !user.IsActive || user.Role == UserRole.Pending)
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.Redirect("/login?error=pending");
            }

            await SignInAsync(http, users, user, auth.CurrentValue.SessionDays);
            return Results.Redirect(NormalizeReturnUrl(http.Request.Query["returnUrl"]));
        }).RequireAuthorization(ArrkPolicies.Authenticated);

        endpoints.MapGet("/account/avatar/{id:int}", async (int id, UserService users, CancellationToken ct) =>
        {
            ArrkUser? user = await users.GetByIdAsync(id, ct);
            if (user?.Avatar is not { Length: > 0 } avatar)
                return Results.NotFound();

            return Results.File(avatar, user.AvatarContentType ?? "image/png");
        }).RequireAuthorization(ArrkPolicies.Authenticated);
    }

    static async Task SignInAsync(HttpContext http, UserService users, ArrkUser user, int? sessionDays = null)
    {
        IReadOnlyList<string> roles = await users.GetRolesAsync(user);
        string securityStamp = await users.GetSecurityStampAsync(user);
        ClaimsIdentity identity = ArrkClaims.Create(
            user, roles, CookieAuthenticationDefaults.AuthenticationScheme, securityStamp: securityStamp);

        int days = sessionDays ?? http.RequestServices.GetRequiredService<IOptionsMonitor<AuthConfig>>().CurrentValue.SessionDays;
        AuthenticationProperties properties = new()
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(Math.Clamp(days, 1, 365))
        };

        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), properties);
    }

    /// <summary>Only allow local, absolute paths as a post-login redirect (prevents open redirects).</summary>
    public static string NormalizeReturnUrl(string? returnUrl) => LocalReturnUrl.Normalize(returnUrl);

    static async Task<bool> IsAntiforgeryValidAsync(
        HttpContext http, Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(http);
            return true;
        }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Keeps the cookie authentication options in step with the live <c>auth</c> configuration so
/// <c>auth.require_https</c> and <c>auth.session_days</c> apply without an app restart.
/// </summary>
internal sealed class AuthCookieConfigurator(IOptionsMonitor<AuthConfig> auth)
    : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(string? name, CookieAuthenticationOptions options) => Configure(options);

    public void Configure(CookieAuthenticationOptions options)
    {
        AuthConfig config = auth.CurrentValue;
        options.Cookie.SecurePolicy = config.RequireHttps
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(Math.Clamp(config.SessionDays, 1, 365));
        options.SlidingExpiration = true;
    }
}

/// <summary>
/// Keeps the Discord upstream OIDC options in step with the live <c>auth.external.discord</c>
/// configuration so enabling it (and supplying its client id/secret) applies without a restart.
/// </summary>
internal sealed class DiscordOidcConfigurator(IOptionsMonitor<AuthConfig> auth)
    : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (!string.Equals(name, ArrkAuthenticationExtensions.DiscordScheme, StringComparison.Ordinal))
            return;

        DiscordConfig discord = auth.CurrentValue.External.Discord;
        options.ClientId = string.IsNullOrWhiteSpace(discord.ClientId) ? "arrk-discord" : discord.ClientId;
        options.ClientSecret = discord.ClientSecret ?? "";
        options.Authority = string.IsNullOrWhiteSpace(discord.Authority) ? "https://discord.com" : discord.Authority;
        options.CallbackPath = string.IsNullOrWhiteSpace(discord.CallbackPath) ? "/signin-discord" : discord.CallbackPath;
    }

    public void Configure(OpenIdConnectOptions options)
    {
    }
}
