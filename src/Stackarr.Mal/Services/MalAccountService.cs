using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stackarr.Core;
using Stackarr.Mal.Auth;
using Stackarr.Mal.Clients;
using Stackarr.Mal.Models;

namespace Stackarr.Mal.Services;

/// <summary>Owns one MAL account per Stackarr user and keeps its OAuth tokens fresh.</summary>
public sealed class MalAccountService(
    IDbContextFactory<StackarrDbContext> dbFactory,
    MalTokenProtector tokens,
    MalClient client,
    TimeProvider time,
    ILogger<MalAccountService> logger)
{
    readonly ConcurrentDictionary<int, SemaphoreSlim> _refreshLocks = new();

    public async Task<MalAccount?> GetAsync(int userId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<MalAccount>().AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
    }

    public async Task<MalAccount?> GetByMalUserIdAsync(int malUserId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Set<MalAccount>().AsNoTracking().FirstOrDefaultAsync(x => x.MalUserId == malUserId, ct);
    }

    public async Task SaveAsync(
        int userId,
        MalUser user,
        MalTokenResponse response,
        CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalAccount? account = await db.Set<MalAccount>().FirstOrDefaultAsync(x => x.UserId == userId, ct);

        if (account is null)
        {
            account = new MalAccount
            {
                UserId = userId,
                LinkedAt = time.GetUtcNow().UtcDateTime
            };
            db.Set<MalAccount>().Add(account);
        }

        account.MalUserId = user.Id;
        account.MalUsername = user.Name;
        account.AccessToken = tokens.Protect(response.AccessToken);
        account.RefreshToken = tokens.Protect(response.RefreshToken);
        account.AccessExpiresAt = time.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(60, response.ExpiresIn));
        account.NeedsReauth = false;
        account.LastError = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Returns a usable bearer token, refreshing it in place when needed.</summary>
    public async Task<string> GetAccessTokenAsync(MalAccount account, CancellationToken ct = default)
    {
        if (time.GetUtcNow().UtcDateTime < account.AccessExpiresAt.AddMinutes(-5))
            return tokens.Unprotect(account.AccessToken);

        SemaphoreSlim gate = _refreshLocks.GetOrAdd(account.UserId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
            MalAccount? current = await db.Set<MalAccount>().FirstOrDefaultAsync(x => x.UserId == account.UserId, ct);
            if (current is null)
                throw new InvalidOperationException("MyAnimeList account is no longer linked.");

            if (time.GetUtcNow().UtcDateTime < current.AccessExpiresAt.AddMinutes(-5))
            {
                account.AccessExpiresAt = current.AccessExpiresAt;
                return tokens.Unprotect(current.AccessToken);
            }

            string refreshToken = tokens.Unprotect(current.RefreshToken);
            if (string.IsNullOrEmpty(refreshToken))
            {
                await MarkNeedsReauthAsync(db, current, "The MyAnimeList refresh token is missing.", ct);
                throw new InvalidOperationException("MyAnimeList needs to be reconnected.");
            }

            try
            {
                MalTokenResponse refreshed = await client.RefreshAsync(refreshToken, ct);
                current.AccessToken = tokens.Protect(refreshed.AccessToken);
                current.RefreshToken = tokens.Protect(
                    string.IsNullOrEmpty(refreshed.RefreshToken) ? refreshToken : refreshed.RefreshToken);
                current.AccessExpiresAt = time.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(60, refreshed.ExpiresIn));
                current.NeedsReauth = false;
                current.LastError = null;
                await db.SaveChangesAsync(ct);

                account.AccessExpiresAt = current.AccessExpiresAt;
                return refreshed.AccessToken;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not refresh MyAnimeList token for user {UserId}", current.UserId);
                await MarkNeedsReauthAsync(db, current, "The MyAnimeList session expired. Reconnect the account.", ct);
                throw new InvalidOperationException("MyAnimeList needs to be reconnected.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SetSyncSettingsAsync(
        int userId,
        bool syncEnabled,
        bool protectProgress,
        CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalAccount? account = await db.Set<MalAccount>().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (account is null)
            return;
        account.SyncEnabled = syncEnabled;
        account.ProtectProgress = protectProgress;
        await db.SaveChangesAsync(ct);
    }

    public async Task TouchPulledAsync(int userId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.Set<MalAccount>()
            .Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastPulledAt, time.GetUtcNow().UtcDateTime), ct);
    }

    public async Task SetLastSyncAsync(
        int userId,
        DateTime at,
        string summary,
        CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalAccount? account = await db.Set<MalAccount>().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (account is null)
            return;
        account.LastSyncedAt = at;
        account.LastSyncSummary = summary;
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordErrorAsync(int userId, string error, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        MalAccount? account = await db.Set<MalAccount>().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (account is null)
            return;
        account.LastError = error.Length > 1000 ? error[..1000] : error;
        await db.SaveChangesAsync(ct);
    }

    public async Task UnlinkAsync(int userId, CancellationToken ct = default)
    {
        await using StackarrDbContext db = await dbFactory.CreateDbContextAsync(ct);
        await db.Set<MalListEntry>().Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Set<MalAccount>().Where(x => x.UserId == userId).ExecuteDeleteAsync(ct);
    }

    static async Task MarkNeedsReauthAsync(
        StackarrDbContext db,
        MalAccount account,
        string error,
        CancellationToken ct)
    {
        account.NeedsReauth = true;
        account.LastError = error;
        await db.SaveChangesAsync(ct);
    }
}
