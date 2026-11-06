using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Arrk.Core.Notifications;

namespace Arrk.Notifications.Discord;

/// <summary>
/// Thin HTTP client for the Discord webhook execute endpoint. Handles rate limiting (HTTP 429)
/// with the <c>retry_after</c> the API returns, and never throws for HTTP-level failures, it
/// returns a <see cref="NotificationDeliveryResult"/> instead.
/// </summary>
public sealed class DiscordWebhookClient(
    IHttpClientFactory httpClientFactory,
    ILogger<DiscordWebhookClient>? logger = null)
{
    public const string ClientName = "arrk-discord";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<NotificationDeliveryResult> SendAsync(
        string webhookUrl,
        DiscordWebhookPayload payload,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return NotificationDeliveryResult.Fail("Discord webhook URL is empty");

        HttpClient client = httpClientFactory.CreateClient(ClientName);
        string json = JsonSerializer.Serialize(payload, JsonOptions);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Post, webhookUrl)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                using HttpResponseMessage response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                    return NotificationDeliveryResult.Ok();

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    double retryAfter = await ReadRetryAfterAsync(response, ct);
                    logger?.LogWarning("Discord rate limited; retrying in {Seconds:F1}s (attempt {Attempt})",
                        retryAfter, attempt);

                    if (attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfter, 0.5, 30)), ct);
                        continue;
                    }

                    return NotificationDeliveryResult.Fail("Discord rate limited repeatedly");
                }

                string body = await response.Content.ReadAsStringAsync(ct);
                return NotificationDeliveryResult.Fail(
                    $"Discord returned {(int)response.StatusCode}: {Truncate(body, 400)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt < 3)
                {
                    logger?.LogWarning(ex, "Discord webhook attempt {Attempt} failed; retrying", attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
                    continue;
                }

                return NotificationDeliveryResult.Fail($"Discord request failed: {ex.Message}");
            }
        }

        return NotificationDeliveryResult.Fail("Discord delivery failed after retries");
    }

    static async Task<double> ReadRetryAfterAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("retry_after", out JsonElement retry))
                return retry.GetDouble();
        }
        catch
        {
            // Fall through to the header / default below.
        }

        if (response.Headers.TryGetValues("Retry-After", out IEnumerable<string>? values)
            && double.TryParse(values.FirstOrDefault(), out double seconds))
        {
            return seconds;
        }

        return 1.0;
    }

    static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
