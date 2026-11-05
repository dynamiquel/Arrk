using Microsoft.EntityFrameworkCore;
using Stackarr.Ai.Data;
using Stackarr.Core;

namespace Stackarr.Ai;

/// <summary>
/// Stores the persona a user picked in the assistant popup. The row is tiny and read once per turn;
/// a failure is non-fatal and falls back to the configured default.
/// </summary>
public class AiPersonaService(
    IDbContextFactory<StackarrDbContext> dbFactory,
    TimeProvider time)
{
    public virtual async Task<string?> GetKeyAsync(int userId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        AiUserPreference? row = await db.Set<AiUserPreference>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return string.IsNullOrWhiteSpace(row?.PersonaKey) ? null : row.PersonaKey;
    }

    public virtual async Task SetKeyAsync(int userId, string personaKey, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        AiUserPreference? row = await db.Set<AiUserPreference>()
            .FirstOrDefaultAsync(x => x.UserId == userId, ct);

        if (row is null)
        {
            db.Set<AiUserPreference>().Add(new AiUserPreference
            {
                UserId = userId,
                PersonaKey = personaKey,
                UpdatedAt = time.GetUtcNow().UtcDateTime
            });
        }
        else
        {
            row.PersonaKey = personaKey;
            row.UpdatedAt = time.GetUtcNow().UtcDateTime;
        }

        await db.SaveChangesAsync(ct);
    }
}
