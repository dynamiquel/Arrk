using Microsoft.EntityFrameworkCore;

namespace Arrk.Core.Services;

/// <summary>A durable key/value setting owned by Arrk.</summary>
public class ArrkSetting
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Reads/writes durable settings (used for generated OIDC client secrets and similar).</summary>
public sealed class SettingsService(IDbContextFactory<ArrkDbContext> dbContextFactory)
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        ArrkSetting? setting = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return setting?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        ArrkSetting? setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new ArrkSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Returns the existing value, or generates, stores and returns a new one.</summary>
    public async Task<string> GetOrCreateAsync(string key, Func<string> generator, CancellationToken ct = default)
    {
        string? existing = await GetAsync(key, ct);
        if (!string.IsNullOrEmpty(existing))
            return existing;

        string created = generator();
        await SetAsync(key, created, ct);
        return created;
    }
}
