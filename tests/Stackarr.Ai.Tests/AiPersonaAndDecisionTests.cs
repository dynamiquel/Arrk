using Stackarr.Ai.Data;

namespace Stackarr.Ai.Tests;

/// <summary>
/// The new persistence: per-user persona selection and the ranking decision's requested item plus
/// the exact model context. Both are shown to an administrator, so they must survive a round-trip.
/// </summary>
public class AiPersonaAndDecisionTests
{
    [Test]
    public async Task PersonaSelection_RoundTrips()
    {
        using TestAiDb db = new();
        AiPersonaService personas = new(db, TimeProvider.System);

        await Assert.That(await personas.GetKeyAsync(7)).IsNull();

        await personas.SetKeyAsync(7, AiPersonas.Plain);
        await Assert.That(await personas.GetKeyAsync(7)).IsEqualTo(AiPersonas.Plain);

        await personas.SetKeyAsync(7, "cortana");
        await Assert.That(await personas.GetKeyAsync(7)).IsEqualTo("cortana");
        await Assert.That(await personas.GetKeyAsync(8)).IsNull();
    }

    [Test]
    public async Task Decision_StoresTheRequestAndTheModelContext()
    {
        using TestAiDb db = new();
        AiDecisionService decisions = new(db, TimeProvider.System);

        await decisions.RecordAsync(new AiDecisionOutcome
        {
            Feature = AiFeatureKeys.ReleaseRanking,
            Operation = "music.albumRank",
            Subject = "42",
            Request = "Portishead — Dummy",
            Context = "Target: album \"Dummy\" by \"Portishead\".\nCandidates:\n1. ...",
            Mode = AiRankingModes.Shadow,
            DeterministicChoice = "Dummy (1994) FLAC",
            AiChoice = "Dummy (Remastered) FLAC",
            Confidence = 0.72,
            Reason = "Remaster of the same edition."
        });

        IReadOnlyList<AiDecisionOutcome> rows = await decisions.GetRecentAsync();
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].Request).IsEqualTo("Portishead — Dummy");
        await Assert.That(rows[0].Context).Contains("Target: album");
        await Assert.That(rows[0].Outcome).IsNull();
    }
}
