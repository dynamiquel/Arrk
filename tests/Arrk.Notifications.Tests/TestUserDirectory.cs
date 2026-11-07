using Arrk.Auth.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Arrk.Notifications.Tests;

/// <summary>Account fixture shared by dispatcher tests; roles remain open-ended strings.</summary>
public sealed class TestUserDirectory : IUserDirectory
{
    public IReadOnlyList<ArrkUser> Accounts { get; init; } = [new() { Id = 7, Username = "reader" }];
    public Dictionary<string, int[]> Roles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IServiceScopeFactory ScopeFactory()
    {
        ServiceCollection services = new();
        services.AddScoped<IUserDirectory>(_ => this);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true })
            .GetRequiredService<IServiceScopeFactory>();
    }

    public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(Accounts.Count > 0);
    public Task<IReadOnlyList<ArrkUser>> GetAllAsync(CancellationToken ct = default) => Task.FromResult(Accounts);
    public Task<ArrkUser?> GetByIdAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(Accounts.FirstOrDefault(u => u.Id == id));
    public Task<ArrkUser?> GetByUsernameAsync(string username, CancellationToken ct = default) =>
        Task.FromResult(Accounts.FirstOrDefault(u => u.Username == username));
    public Task<IReadOnlyList<string>> GetRolesAsync(ArrkUser user) =>
        Task.FromResult<IReadOnlyList<string>>([.. Roles.Where(r => r.Value.Contains(user.Id)).Select(r => r.Key)]);
    public Task<IReadOnlyList<ArrkUser>> GetUsersInRoleAsync(string role, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ArrkUser>>(Roles.TryGetValue(role, out int[]? ids)
            ? [.. Accounts.Where(u => ids.Contains(u.Id))] : []);
    public Task<bool> IsInRoleAsync(ArrkUser user, string role) =>
        Task.FromResult(Roles.TryGetValue(role, out int[]? ids) && ids.Contains(user.Id));
}
