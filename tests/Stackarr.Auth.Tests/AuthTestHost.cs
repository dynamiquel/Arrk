using Stackarr.Core.Services;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;
using Stackarr.App.Services;
using Stackarr.Auth.Services;
using Stackarr.Core;
using Stackarr.Auth.Abstractions;
using Stackarr.Core.Configuration;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Stackarr.Auth.Tests;

/// <summary>
/// A real ASP.NET Core host (in-memory TestServer) wired with the exact production Identity,
/// cookie-authentication and OpenIddict-provider setup, backed by a real SQLite database.
/// </summary>
public sealed class AuthTestHost : IAsyncDisposable
{
    public const string AdminPassword = "admin-test-pass";

    readonly TestDatabase _db;
    readonly bool _ownsDb;
    readonly string _oidcDir;
    readonly CookieHandler _cookieHandler;
    readonly List<IServiceScope> _scopes = [];

    public WebApplication App { get; }
    public HttpClient Client { get; }

    // Production resolves UserService (and its scoped Identity DbContext) per request. Hand out a
    // fresh scope per access so reads observe committed writes instead of a cached tracked entity.
    public UserService Users
    {
        get
        {
            IServiceScope scope = App.Services.CreateScope();
            _scopes.Add(scope);
            return scope.ServiceProvider.GetRequiredService<UserService>();
        }
    }

    public IServiceProvider Services => App.Services;

    /// <summary>A scoped registration service (fresh scope per access, like <see cref="Users"/>).</summary>
    public RegistrationService Registration
    {
        get
        {
            IServiceScope scope = App.Services.CreateScope();
            _scopes.Add(scope);
            return scope.ServiceProvider.GetRequiredService<RegistrationService>();
        }
    }

    AuthTestHost(
        WebApplication app, HttpClient client, TestDatabase db, bool ownsDb,
        string oidcDir, CookieHandler cookieHandler)
    {
        App = app;
        Client = client;
        _db = db;
        _ownsDb = ownsDb;
        _oidcDir = oidcDir;
        _cookieHandler = cookieHandler;
    }

    static readonly Uri BaseUri = new("http://localhost/");

    /// <summary>Reads a cookie the client would send (used to simulate a browser session).</summary>
    public string? GetCookie(string name) => _cookieHandler.Cookies.GetCookies(BaseUri)[name]?.Value;

    /// <summary>Injects a cookie into the client (used to replay a session after a restart).</summary>
    public void SetCookie(string name, string value) =>
        _cookieHandler.Cookies.Add(new Cookie(name, value, "/", "localhost"));

    public static async Task<AuthTestHost> CreateAsync(
        Action<Dictionary<string, string?>>? configureSettings = null,
        string? dataProtectionDir = null,
        Action<IServiceCollection>? configureServices = null,
        TestDatabase? database = null)
    {
        TestDatabase db = database ?? new();
        bool ownsDb = database is null;
        string oidcDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"stackarr-oidc-{Guid.NewGuid():N}");

        Dictionary<string, string?> settings = new()
        {
            ["auth:adminUsername"] = "admin",
            ["auth:adminPassword"] = AdminPassword,
            ["auth:allowPasswordLogin"] = "true",
            ["auth:registrationMode"] = "Approval",
            ["auth:authRateLimitPerMinute"] = "10000",
            ["auth:provider:enabled"] = "true",
            ["routes:publicUrl"] = "http://localhost"
        };
        configureSettings?.Invoke(settings);

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole().SetMinimumLevel(LogLevel.Information);
        builder.Configuration.AddInMemoryCollection(settings);

        builder.Services.Configure<AuthConfig>(builder.Configuration.GetSection(AuthConfig.SectionName));
        builder.Services.Configure<RoutesConfig>(builder.Configuration.GetSection("routes"));
        builder.Services.AddDbContextFactory<StackarrDbContext>(o => o
            .UseSqlite(db.ConnectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        builder.Services.AddStackarrIdentity();
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddStackarrAuthentication(builder.Configuration);

        // The same admin-only config endpoints the production host maps.
        ConfigSectionRegistry registry = new();
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(sp => new ConfigSchemaGenerator(registry, sp));
        builder.Services.AddSingleton(sp =>
            new ConfigValidator(sp.GetRequiredService<ConfigSchemaGenerator>(), registry, sp));

        if (!string.IsNullOrEmpty(dataProtectionDir))
        {
            Directory.CreateDirectory(dataProtectionDir);
            builder.Services.AddDataProtection()
                .SetApplicationName("stackarr")
                .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionDir));
        }

        AuthConfig auth = builder.Configuration.GetSection(AuthConfig.SectionName).Get<AuthConfig>() ?? new();
        RoutesConfig routes = builder.Configuration.GetSection("routes").Get<RoutesConfig>() ?? new();
        builder.Services.AddStackarrOpenIddict(auth, routes, oidcDir);
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapStackarrAuthEndpoints();
        app.MapStackarrOpenIddictEndpoints();
        app.MapStackarrConfigEndpoints();
        app.MapGet("/admin", () => "admin-ok").RequireAuthorization(StackarrPolicies.AdminOnly);
        app.MapGet("/whoami", (HttpContext http) => http.User.Identity?.Name ?? "anon")
            .RequireAuthorization(StackarrPolicies.Authenticated);

        await app.StartAsync();

        TestServer server = (TestServer)app.Services.GetRequiredService<IServer>();
        CookieHandler handler = new()
        {
            InnerHandler = server.CreateHandler(),
            BaseAddress = new Uri("http://localhost/")
        };
        HttpClient client = new(handler) { BaseAddress = new Uri("http://localhost/") };

        return new AuthTestHost(app, client, db, ownsDb, oidcDir, handler);
    }

    /// <summary>Registers an OpenIddict client (authorization code + PKCE) for provider tests.</summary>
    public async Task EnsureClientAsync(string clientId, string secret, string redirectUri)
    {
        IOpenIddictApplicationManager manager = Services.GetRequiredService<IOpenIddictApplicationManager>();

        OpenIddictApplicationDescriptor descriptor = new()
        {
            ClientId = clientId,
            ClientSecret = secret,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = clientId
        };
        descriptor.RedirectUris.Add(new Uri(redirectUri));
        foreach (string scope in new[] { Scopes.OpenId, Scopes.Profile, Scopes.Email, "roles" })
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
        descriptor.Permissions.Add(Requirements.Features.ProofKeyForCodeExchange);

        object? existing = await manager.FindByClientIdAsync(clientId);
        if (existing is null)
            await manager.CreateAsync(descriptor);
        else
            await manager.UpdateAsync(existing, descriptor);
    }

    /// <summary>Signs in the way a browser does: GET the form, read its antiforgery token, POST it.</summary>
    public async Task<HttpResponseMessage> LoginAsync(string username, string password, string returnUrl = "/")
    {
        HttpResponseMessage page = await Client.GetAsync("/login");
        string html = page.IsSuccessStatusCode ? await page.Content.ReadAsStringAsync() : "";
        return await Client.PostAsync(
            "/account/login",
            LoginForm(username, password, returnUrl, ExtractAntiforgeryToken(html)));
    }

    /// <summary>Submits the sign-up form with the antiforgery token from the real page.</summary>
    public async Task<HttpResponseMessage> RegisterAsync(
        string username, string password, string? confirmPassword = null)
    {
        HttpResponseMessage page = await Client.GetAsync("/register");
        string html = page.IsSuccessStatusCode ? await page.Content.ReadAsStringAsync() : "";
        return await Client.PostAsync(
            "/account/register",
            RegisterForm(username, password, confirmPassword, ExtractAntiforgeryToken(html)));
    }

    public static string ExtractAntiforgeryToken(string html)
    {
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : "";
    }

    public static FormUrlEncodedContent LoginForm(
        string username, string password, string returnUrl = "/", string? antiforgeryToken = null)
        => new(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["returnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = antiforgeryToken ?? ""
        });

    public static FormUrlEncodedContent RegisterForm(
        string username, string password, string? confirmPassword = null, string? antiforgeryToken = null)
        => new(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["confirmPassword"] = confirmPassword ?? password,
            ["__RequestVerificationToken"] = antiforgeryToken ?? ""
        });

    public async ValueTask DisposeAsync()
    {
        foreach (IServiceScope scope in _scopes)
            scope.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        if (_ownsDb)
            await _db.DisposeAsync();

        try
        {
            if (Directory.Exists(_oidcDir))
                Directory.Delete(_oidcDir, recursive: true);
        }
        catch
        {
            /* best effort */
        }
    }
}

/// <summary>
/// Persists <c>Set-Cookie</c> headers between requests so the cookie auth flow behaves like a
/// browser (the TestServer handler does not maintain a cookie container).
/// </summary>
internal sealed class CookieHandler : DelegatingHandler
{
    public CookieContainer Cookies { get; } = new();

    public required Uri BaseAddress { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? BaseAddress;

        string cookieHeader = Cookies.GetCookieHeader(uri);
        if (!string.IsNullOrEmpty(cookieHeader))
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? setCookies))
        {
            foreach (string setCookie in setCookies)
                Cookies.SetCookies(uri, setCookie);
        }

        return response;
    }
}
