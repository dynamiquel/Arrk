using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Arrk.Notifications;

namespace Arrk.Notifications.Webhook;

/// <summary>
/// A transport-agnostic JSON webhook channel. Every notification becomes a single POST of a
/// stable JSON document, which makes it compatible with Apprise, n8n, Home Assistant, custom
/// endpoints, and anything else that can receive a webhook.
/// </summary>
public sealed class WebhookNotificationClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<WebhookConfig> config,
    NotificationEventRegistry registry,
    NotificationStackInfo stack,
    ILogger<WebhookNotificationClient> logger) : INotificationClient
{
    public const string ChannelName = "webhook";
    public const string ClientName = "arrk-webhook";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Name => ChannelName;

    public async IAsyncEnumerable<NotificationDeliveryResult> SendAsync(
        NotificationMessage message,
        NotificationRecipient recipient,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        WebhookConfig webhook = config.CurrentValue;
        if (recipient.UserId is not null || !webhook.Enabled)
        {
            yield return NotificationDeliveryResult.Skip();
            yield break;
        }

        if (NotificationFilter.IsExcluded(webhook.Exclude, message.Key))
        {
            logger.LogDebug("Webhook delivery skipped for {Topic} by exclude filter", message.Key);
            yield return NotificationDeliveryResult.Skip();
            yield break;
        }

        IReadOnlyList<string> webhookUrls = WebhookRouting.Resolve(webhook.Routing, recipient, message.Key);
        if (webhookUrls.Count == 0)
        {
            yield return NotificationDeliveryResult.Skip();
            yield break;
        }

        NotificationStyle style = registry.StyleFor(message.Key).Merge(message.Style);

        object payload = new
        {
            stack = new
            {
                name = stack.Name,
                url = stack.PublicUrl
            },
            topic = message.Key,
            group = registry.GroupOf(message.Key),
            domain = registry.DomainOf(message.Key),
            title = message.Title,
            body = message.Body,
            severity = message.Severity.ToString(),
            emoji = style.Emoji,
            color = style.Color,
            url = stack.AbsoluteUrl(message.Url),
            source = message.Source,
            sourceName = message.SourceName,
            timestamp = message.Timestamp,
            data = message.Data,
            fields = message.Fields.ToDictionary(f => f.Name, f => f.Value)
        };

        HttpClient client = httpClientFactory.CreateClient(ClientName);
        string json = JsonSerializer.Serialize(payload, JsonOptions);

        foreach (string webhookUrl in webhookUrls)
        {
            ct.ThrowIfCancellationRequested();
            yield return await SendToAsync(client, webhookUrl, webhook.AuthHeader, json, message.Key, ct);
        }
    }

    async Task<NotificationDeliveryResult> SendToAsync(
        HttpClient client, string webhookUrl, string? authHeader, string json, string topic, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, webhookUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            if (!string.IsNullOrWhiteSpace(authHeader))
                request.Headers.TryAddWithoutValidation("Authorization", authHeader);

            using HttpResponseMessage response = await client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
                return NotificationDeliveryResult.Ok();

            string body = await response.Content.ReadAsStringAsync(ct);
            return NotificationDeliveryResult.Fail(
                $"Webhook returned {(int)response.StatusCode}: {Truncate(body, 400)}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Webhook delivery failed for {Key}", topic);
            return NotificationDeliveryResult.Fail($"Webhook request failed: {ex.Message}");
        }
    }

    static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
