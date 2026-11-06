using Microsoft.EntityFrameworkCore;
using Arrk.Auth.Abstractions;

namespace Arrk.Core.Services;

/// <summary>
/// Per-account presentation preferences that are too large for the generic key/value
/// <see cref="ArrkSetting"/> store: the dashboard's tile layout (YAML) and the account's
/// custom CSS overrides.
/// </summary>
public class UserUiPreference
{
    public int UserId { get; set; }

    public ArrkUser? User { get; set; }

    /// <summary>The dashboard layout as YAML, the single source of truth for the Tile Editor.</summary>
    public string DashboardYaml { get; set; } = "";

    /// <summary>CSS appended after the shared theme for this account only.</summary>
    public string CustomCss { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Reads and writes <see cref="UserUiPreference"/> for one account at a time.</summary>
public sealed class UserUiPreferenceService(IDbContextFactory<ArrkDbContext> dbContextFactory)
{
    public async Task<UserUiPreference?> GetAsync(int userId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.UserUiPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
    }

    public async Task SaveDashboardAsync(int userId, string yaml, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserUiPreference preference = await UpsertAsync(db, userId, ct);
        preference.DashboardYaml = yaml;
        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SaveCssAsync(int userId, string css, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserUiPreference preference = await UpsertAsync(db, userId, ct);
        preference.CustomCss = css;
        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes the account's custom layout and CSS (used by the editors' reset actions).</summary>
    public async Task ClearDashboardAsync(int userId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserUiPreference? preference = await db.UserUiPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (preference is null)
            return;

        preference.DashboardYaml = "";
        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearCssAsync(int userId, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserUiPreference? preference = await db.UserUiPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (preference is null)
            return;

        preference.CustomCss = "";
        preference.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    static async Task<UserUiPreference> UpsertAsync(ArrkDbContext db, int userId, CancellationToken ct)
    {
        UserUiPreference? preference = await db.UserUiPreferences.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (preference is not null)
            return preference;

        preference = new() { UserId = userId, UpdatedAt = DateTime.UtcNow };
        db.UserUiPreferences.Add(preference);
        return preference;
    }
}
