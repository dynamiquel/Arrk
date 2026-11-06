using System.Text.Json;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Arrk.Core;
using Arrk.Core.Configuration;
using Arrk.Core.Services;
using Arrk.Tvarr.Clients;
using Arrk.Tvarr.Models;
using Arrk.Transmission;

namespace Arrk.Tvarr.Jobs;

public class DownloadClientSyncJob(
    ServiceRegistry serviceRegistry,
    RadarrClient radarr,
    SonarrClient sonarr,
    ArrkInstance arrk,
    IConfiguration configuration,
    ILogger<DownloadClientSyncJob> logger)
{
    public const string DefaultCron = "*/30 * * * * *";

    [JobDisplayName("Download Client Sync")]
    public async Task Execute(CancellationToken ct)
    {
        Service? tvService = serviceRegistry.Get(TvServiceDefinition.StaticKey);
        Service? torrentService = serviceRegistry.Get(TransmissionServiceDefinition.StaticKey);

        if (tvService?.State.IsHealthy != true || torrentService?.State.IsHealthy != true)
        {
            return;
        }

        // A VPN-routed downloader has no DNS record of its own; reach it through Gluetun.
        string host = arrk.ResolveServiceHost(
            arrk.GetContainerName("transmission"), configuration.IsTorrentVpnRouted());
        int port = 9091;
        string username = TransmissionServiceDefinition.DesiredUsername;
        string password = TransmissionServiceDefinition.DesiredPassword;

        await SyncDownloadClient(radarr, host, port, username, password, "Radarr", ct);
        await SyncDownloadClient(sonarr, host, port, username, password, "Sonarr", ct);
    }

    async Task SyncDownloadClient(
        ArrClient client,
        string host, int port, string username, string password,
        string serviceName, CancellationToken ct)
    {
        List<DownloadClientDto> existing = await client.GetDownloadClients(ct);

        DownloadClientDto? transmission = existing.FirstOrDefault(
            c => c.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase));

        if (transmission != null && MatchesExpected(transmission, host, port, username, password))
        {
            return;
        }

        if (transmission != null)
        {
            try
            {
                await client.DeleteDownloadClient(transmission.Id, ct);
                logger.LogInformation("Deleted misconfigured Transmission download client (ID {Id}) from {Service}", transmission.Id, serviceName);
            }
            catch (HttpRequestException ex)
            {
                logger.LogWarning("Failed to delete existing Transmission download client from {Service}: {Reason}", serviceName, ex.Message);
                return;
            }
        }

        logger.LogInformation("Creating Transmission download client in {Service}", serviceName);

        await client.CreateDownloadClient(new
        {
            name = "Transmission",
            enable = true,
            implementation = "Transmission",
            configContract = "TransmissionSettings",
            protocol = "torrent",
            priority = 1,
            removeCompletedDownloads = true,
            removeFailedDownloads = true,
            fields = new object[]
            {
                new { name = "host", value = host },
                new { name = "port", value = (double)port },
                new { name = "username", value = username },
                new { name = "password", value = password },
                new { name = "movieCategory", value = "movies" },
                new { name = "tvCategory", value = "tv" },
                new { name = "recentTvPriority", value = -1.0 },
                new { name = "olderTvPriority", value = -1.0 },
                new { name = "recentMoviePriority", value = -1.0 },
                new { name = "olderMoviePriority", value = -1.0 }
            }
        }, ct);
    }

    public static bool MatchesExpected(
        DownloadClientDto client, string host, int port, string username, string password)
    {
        if (!client.Implementation.Equals("Transmission", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!client.Enabled)
            return false;
        if (!client.RemoveCompletedDownloads)
            return false;
        if (!client.RemoveFailedDownloads)
            return false;

        Dictionary<string, object?> fields = client.Fields.ToDictionary(f => f.Name, f => f.Value);

        return FieldEquals(fields, "host", host)
            && FieldEquals(fields, "port", (double)port)
            && FieldEquals(fields, "username", username)
            && FieldEquals(fields, "password", password);
    }

    static bool FieldEquals(Dictionary<string, object?> fields, string name, object expected)
    {
        if (!fields.TryGetValue(name, out object? actual))
            return false;

        // Arr masks secret fields (e.g. "password") when returning settings. A masked value cannot
        // be compared against the configured secret, so treat it as matching rather than forcing a
        // needless delete/recreate on every sync.
        if (IsMasked(actual))
            return true;

        if (expected is string expectedStr)
        {
            return string.Equals(expectedStr, AsString(actual), StringComparison.OrdinalIgnoreCase);
        }

        if (expected is double expectedDbl)
        {
            return TryAsDouble(actual, out double actualDbl)
                && Math.Abs(expectedDbl - actualDbl) < 0.01;
        }

        return expected.Equals(actual);
    }

    /// <summary>Normalises a JSON field value (which may be a <see cref="JsonElement"/>) to a string.</summary>
    static string AsString(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.GetRawText()
            };
        }

        return value?.ToString() ?? "";
    }

    static bool TryAsDouble(object? value, out double result)
    {
        switch (value)
        {
            case JsonElement element when element.ValueKind == JsonValueKind.Number:
                return element.TryGetDouble(out result);
            case JsonElement element when element.ValueKind == JsonValueKind.String:
                return double.TryParse(element.GetString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out result);
            case double d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    static bool IsMasked(object? value)
    {
        string? text = value switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            string s => s,
            _ => null
        };

        return !string.IsNullOrEmpty(text) && text.All(c => c == '*');
    }
}
