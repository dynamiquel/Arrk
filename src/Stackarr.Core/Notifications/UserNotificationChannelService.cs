using Microsoft.EntityFrameworkCore;

namespace Stackarr.Core.Notifications;

/// <summary>
/// Stores per-account transport opt-ins and small delivery caches (a Discord DM channel id, for
/// example). Kept in Core so every transport shares one consent record and future transports do
/// not invent their own table.
/// </summary>
public sealed class UserNotificationChannelService(
    IDbContextFactory<StackarrDbContext> dbContextFactory,
    TimeProvider time)
{
    public async Task<UserNotificationChannel?> GetAsync(
        int userId,
        string channel,
        CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<UserNotificationChannel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Channel == channel, ct);
    }

    public async Task<IReadOnlyList<UserNotificationChannel>> GetForUserAsync(
        int userId,
        CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<UserNotificationChannel>()
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.Channel)
            .ToListAsync(ct);
    }

    /// <summary>
    /// A transport with no row is on by default; only an explicit <see cref="UserNotificationChannel.Enabled"/>
    /// false opts the user out.
    /// </summary>
    public async Task<bool> IsEnabledAsync(int userId, string channel, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserNotificationChannel? row = await db.Set<UserNotificationChannel>()
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Channel == channel, ct);
        return row is null || row.Enabled;
    }

    public async Task SetEnabledAsync(
        int userId,
        string channel,
        bool enabled,
        CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserNotificationChannel? row = await db.Set<UserNotificationChannel>()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Channel == channel, ct);

        if (row is null)
        {
            row = new UserNotificationChannel
            {
                UserId = userId,
                Channel = channel,
                UpdatedAt = now
            };
            db.Set<UserNotificationChannel>().Add(row);
        }

        row.Enabled = enabled;
        row.UpdatedAt = now;
        row.LastError = null;
        if (!enabled)
            row.Destination = null;

        await db.SaveChangesAsync(ct);
    }

    public async Task SetDestinationAsync(
        int userId,
        string channel,
        string? destination,
        CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserNotificationChannel? row = await db.Set<UserNotificationChannel>()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Channel == channel, ct);
        if (row is null)
            return;

        row.Destination = destination;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordSuccessAsync(
        int userId,
        string channel,
        string? destination,
        CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserNotificationChannel? row = await db.Set<UserNotificationChannel>()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Channel == channel, ct);
        if (row is null)
        {
            // Direct messages are on by default, so the first successful send materialises the
            // consent row and the cached destination instead of leaving it implicit.
            row = new UserNotificationChannel
            {
                UserId = userId,
                Channel = channel,
                Enabled = true
            };
            db.Set<UserNotificationChannel>().Add(row);
        }

        row.Destination = destination;
        row.LastError = null;
        row.LastSentAt = now;
        row.VerifiedAt ??= now;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordFailureAsync(
        int userId,
        string channel,
        string error,
        bool clearDestination = false,
        CancellationToken ct = default)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        await using StackarrDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        UserNotificationChannel? row = await db.Set<UserNotificationChannel>()
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Channel == channel, ct);
        if (row is null)
        {
            row = new UserNotificationChannel
            {
                UserId = userId,
                Channel = channel,
                Enabled = true
            };
            db.Set<UserNotificationChannel>().Add(row);
        }

        row.LastError = error.Length <= 1000 ? error : error[..1000];
        if (clearDestination)
            row.Destination = null;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }
}
