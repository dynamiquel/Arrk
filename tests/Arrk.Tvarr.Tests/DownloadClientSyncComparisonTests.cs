using Arrk.Tvarr.Jobs;
using Arrk.Tvarr.Models;

namespace Arrk.Tvarr.Tests;

/// <summary>
/// Covers the Radarr and Sonarr download-client comparison. Servers mask secret fields (the password
/// returns as "********") and return numeric fields such as port as JSON numbers. The comparison must
/// tolerate both; otherwise an unchanged client looks changed and is deleted and recreated on every
/// job run.
///
/// The JSON below mirrors a real Radarr GET response.
/// </summary>
public class DownloadClientSyncComparisonTests
{
    const string RealServerJson = """
    {
      "id": 1,
      "name": "Transmission",
      "implementation": "Transmission",
      "configContract": "TransmissionSettings",
      "enable": true,
      "removeCompletedDownloads": true,
      "removeFailedDownloads": true,
      "fields": [
        { "name": "host", "value": "stackarr-it-transmission" },
        { "name": "port", "value": 9091 },
        { "name": "username", "value": "transmission" },
        { "name": "password", "value": "********" },
        { "name": "movieCategory", "value": "movies" },
        { "name": "tvCategory", "value": "tv" }
      ]
    }
    """;

    static DownloadClientDto FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<DownloadClientDto>(
            json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Test]
    public async Task UnchangedClient_WithMaskedPassword_Matches()
    {
        DownloadClientDto stored = FromJson(RealServerJson);

        bool matches = DownloadClientSyncJob.MatchesExpected(
            stored, "stackarr-it-transmission", 9091, "transmission", "arrk");

        await Assert.That(matches).IsTrue();
    }

    [Test]
    public async Task PasswordReturnedInPlainText_IsAccepted()
    {
        DownloadClientDto stored = FromJson(RealServerJson);
        stored.Fields.First(f => f.Name == "password").Value = "arrk";

        await Assert.That(DownloadClientSyncJob.MatchesExpected(
            stored, "stackarr-it-transmission", 9091, "transmission", "arrk")).IsTrue();
    }

    [Test]
    public async Task DifferentHost_IsDetected()
    {
        DownloadClientDto stored = FromJson(RealServerJson);

        await Assert.That(DownloadClientSyncJob.MatchesExpected(
            stored, "other-host", 9091, "transmission", "arrk")).IsFalse();
    }

    [Test]
    public async Task DifferentPort_IsDetected()
    {
        DownloadClientDto stored = FromJson(RealServerJson);

        await Assert.That(DownloadClientSyncJob.MatchesExpected(
            stored, "stackarr-it-transmission", 9999, "transmission", "arrk")).IsFalse();
    }

    [Test]
    public async Task DifferentUsername_IsDetected()
    {
        DownloadClientDto stored = FromJson(RealServerJson);

        await Assert.That(DownloadClientSyncJob.MatchesExpected(
            stored, "stackarr-it-transmission", 9091, "someone-else", "arrk")).IsFalse();
    }

    [Test]
    public async Task DisabledClient_IsDetected()
    {
        DownloadClientDto stored = FromJson(RealServerJson);
        stored.Enabled = false;

        await Assert.That(DownloadClientSyncJob.MatchesExpected(
            stored, "stackarr-it-transmission", 9091, "transmission", "arrk")).IsFalse();
    }

    [Test]
    public async Task WrongImplementation_IsDetected()
    {
        DownloadClientDto stored = FromJson(RealServerJson);
        stored.Implementation = "QBittorrent";

        await Assert.That(DownloadClientSyncJob.MatchesExpected(
            stored, "stackarr-it-transmission", 9091, "transmission", "arrk")).IsFalse();
    }
}
