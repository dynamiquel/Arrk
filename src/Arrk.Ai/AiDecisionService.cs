using Microsoft.EntityFrameworkCore;
using Arrk.Ai.Data;
using Arrk.Core;

namespace Arrk.Ai;

/// <summary>Comparison of AI choices against deterministic choices over a window.</summary>
public sealed record AiDecisionSummary(
    int Decisions,
    int Applied,
    int AgreementCount,
    int Imported,
    int Mismatched,
    int Failed,
    int Pending)
{
    /// <summary>Share of decisions where the model agreed with the deterministic pick.</summary>
    public double AgreementRate => Decisions == 0 ? 0 : (double)AgreementCount / Decisions;
}

/// <summary>
/// Records ranking decisions and resolves their outcomes, so an AI pick can be compared with the
/// deterministic pick.
/// </summary>
public sealed class AiDecisionService(
    IDbContextFactory<ArrkDbContext> dbContextFactory,
    TimeProvider time)
{
    public async Task RecordAsync(AiDecisionOutcome decision, CancellationToken ct = default)
    {
        decision.Timestamp = decision.Timestamp == default
            ? time.GetUtcNow().UtcDateTime
            : decision.Timestamp;

        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        db.Set<AiDecisionOutcome>().Add(decision);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks the latest decision for a subject/release with its real outcome. Never throws: outcome
    /// accounting must not break an import.
    /// </summary>
    public async Task ResolveAsync(
        string feature,
        string subject,
        string releaseTitle,
        string outcome,
        CancellationToken ct = default)
    {
        try
        {
            await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
            AiDecisionOutcome? decision = await db.Set<AiDecisionOutcome>()
                .Where(x => x.Feature == feature && x.Subject == subject && x.Outcome == null)
                .Where(x => x.AiChoice == releaseTitle || x.DeterministicChoice == releaseTitle)
                .OrderByDescending(x => x.Id)
                .FirstOrDefaultAsync(ct);

            if (decision is null)
                return;

            decision.Outcome = outcome;
            decision.OutcomeAt = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // Best-effort telemetry.
        }
    }

    public async Task<IReadOnlyList<AiDecisionOutcome>> GetRecentAsync(int limit = 25, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        return await db.Set<AiDecisionOutcome>()
            .AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);
    }

    public async Task<AiDecisionSummary> GetSummaryAsync(DateTime since, CancellationToken ct = default)
    {
        await using ArrkDbContext db = await dbContextFactory.CreateDbContextAsync(ct);
        List<AiDecisionOutcome> rows = await db.Set<AiDecisionOutcome>()
            .AsNoTracking()
            .Where(x => x.Timestamp >= since)
            .ToListAsync(ct);

        return new AiDecisionSummary(
            rows.Count,
            rows.Count(x => x.Applied),
            rows.Count(x => string.Equals(x.AiChoice, x.DeterministicChoice, StringComparison.Ordinal)),
            rows.Count(x => x.Outcome == "imported"),
            rows.Count(x => x.Outcome == "mismatch"),
            rows.Count(x => x.Outcome == "failed"),
            rows.Count(x => x.Outcome is null));
    }
}
