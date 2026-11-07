using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arrk.Notifications.Discord;

/// <summary>
/// Thin Discord REST client for bot direct messages. Deliberately has no gateway or Discord SDK
/// dependency: bots only need <c>POST /users/@me/channels</c> followed by
/// <c>POST /channels/{id}/messages</c>. Rate limits are retried with Discord's <c>retry_after</c>.
/// </summary>
public sealed class DiscordBotClient(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<DiscordNotificationConfig> config,
    ILogger<DiscordBotClient> logger)
{
    public const string ClientName = "arrk-discord-bot";

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    DiscordDirectMessageConfig Settings => config.CurrentValue.DirectMessages;

    public bool IsConfigured
    {
        get
        {
            DiscordDirectMessageConfig settings = Settings;
            return settings.Enabled
                && !string.IsNullOrWhiteSpace(settings.BotToken)
                && Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out _);
        }
    }

    /// <summary>Invite URL for the bot, or null when no application id is configured.</summary>
    public string? InviteUrl()
    {
        string applicationId = Settings.ApplicationId.Trim();
        return string.IsNullOrWhiteSpace(applicationId)
            ? null
            : $"https://discord.com/oauth2/authorize?client_id={applicationId}&scope=bot&permissions=0";
    }

    public async Task<DiscordBotInfo?> GetBotAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;

        HttpResponseMessage? response = await SendAsync(
            HttpMethod.Get, "/users/@me", null, allowRateLimitRetry: true, ct);
        if (response is null)
            return null;

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return null;

            DiscordUserResponse? user = await DeserializeAsync<DiscordUserResponse>(response, ct);
            return user is null ? null : new DiscordBotInfo(user.Id, user.Username);
        }
    }

    /// <summary>
    /// Sends a DM to a Discord user. <paramref name="existingChannelId"/> is a cached DM channel
    /// from a previous send; when it is stale the client creates a fresh one.
    /// </summary>
    public async Task<DiscordDirectMessageResult> SendDirectMessageAsync(
        string discordUserId,
        DiscordMessagePayload payload,
        string? existingChannelId,
        CancellationToken ct = default)
    {
        if (!IsConfigured)
            return DiscordDirectMessageResult.Fail("Discord direct messages are not configured.");
        if (string.IsNullOrWhiteSpace(discordUserId))
            return DiscordDirectMessageResult.Fail("The account has no linked Discord identity.");

        string? channelId = existingChannelId;
        if (string.IsNullOrWhiteSpace(channelId))
        {
            (channelId, string? createError) = await CreateDmChannelAsync(discordUserId, ct);
            if (channelId is null)
                return DiscordDirectMessageResult.Fail(createError ?? "Discord could not open a DM channel.");
        }

        DiscordDirectMessageResult result = await SendToChannelAsync(channelId, payload, ct);
        if (result.Success || !result.ChannelMissing)
            return result;

        // The cached DM channel is gone (the user deleted it, closed DMs, or the bot was removed).
        // Create a fresh one and retry once.
        (string? freshChannel, string? freshError) = await CreateDmChannelAsync(discordUserId, ct);
        if (freshChannel is null)
            return DiscordDirectMessageResult.Fail(freshError ?? "Discord could not open a DM channel.");

        return await SendToChannelAsync(freshChannel, payload, ct);
    }

    async Task<(string? ChannelId, string? Error)> CreateDmChannelAsync(string discordUserId, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(new DiscordCreateDmRequest { RecipientId = discordUserId }, JsonOptions);
        HttpResponseMessage? response = await SendAsync(
            HttpMethod.Post, "/users/@me/channels", json, allowRateLimitRetry: true, ct);
        if (response is null)
            return (null, "Discord request failed.");

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return (null, await FriendlyErrorAsync(response, ct));

            DiscordChannelResponse? channel = await DeserializeAsync<DiscordChannelResponse>(response, ct);
            return channel is null || string.IsNullOrWhiteSpace(channel.Id)
                ? (null, "Discord returned an empty DM channel.")
                : (channel.Id, null);
        }
    }

    async Task<DiscordDirectMessageResult> SendToChannelAsync(
        string channelId,
        DiscordMessagePayload payload,
        CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload, JsonOptions);
        HttpResponseMessage? response = await SendAsync(
            HttpMethod.Post, $"/channels/{channelId}/messages", json, allowRateLimitRetry: true, ct);
        if (response is null)
            return DiscordDirectMessageResult.Fail("Discord request failed.");

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return DiscordDirectMessageResult.Ok(channelId);

            bool missing = response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden;
            string error = await FriendlyErrorAsync(response, ct);
            // Only a stale/forbidden channel is retried by creating a fresh DM channel.
            return DiscordDirectMessageResult.Fail(error, channelMissing: missing);
        }
    }

    async Task<HttpResponseMessage?> SendAsync(
        HttpMethod method,
        string path,
        string? json,
        bool allowRateLimitRetry,
        CancellationToken ct)
    {
        DiscordDirectMessageConfig settings = Settings;
        string baseUrl = settings.ApiBaseUrl.TrimEnd('/');
        HttpClient client = httpClientFactory.CreateClient(ClientName);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using HttpRequestMessage request = new(method, baseUrl + path);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bot {settings.BotToken}");
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                HttpResponseMessage response = await client.SendAsync(request, ct);
                if (response.StatusCode != HttpStatusCode.TooManyRequests || !allowRateLimitRetry || attempt == 3)
                    return response;

                double retryAfter = await ReadRetryAfterAsync(response, ct);
                response.Dispose();
                logger.LogWarning(
                    "Discord rate limited; retrying in {Seconds:F1}s (attempt {Attempt})", retryAfter, attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfter, 0.5, 30)), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (attempt == 3)
                {
                    logger.LogWarning(ex, "Discord request failed after retries");
                    return null;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct);
            }
        }

        return null;
    }

    static async Task<string> FriendlyErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        int code = 0;
        string message = "";
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("code", out JsonElement codeElement))
                code = codeElement.GetInt32();
            if (doc.RootElement.TryGetProperty("message", out JsonElement messageElement))
                message = messageElement.GetString() ?? "";
        }
        catch
        {
            // Not JSON; fall through to the status text.
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return "Discord rejected the bot token. Check notifications.discord.directMessages.botToken.";

        if (code == 50007 || response.StatusCode == HttpStatusCode.Forbidden)
        {
            return "Discord cannot DM this user. The user and bot must share a Discord server, and the user must allow direct messages from server members.";
        }

        string detail = string.IsNullOrWhiteSpace(message) ? body : message;
        return $"Discord returned {(int)response.StatusCode}: {Truncate(detail, 400)}";
    }

    static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
        }
        catch
        {
            return default;
        }
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
            // Fall through to header/default.
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

public sealed record DiscordBotInfo(string Id, string Username);

public sealed record DiscordDirectMessageResult(
    bool Success,
    string? ChannelId = null,
    string? Error = null,
    bool ChannelMissing = false)
{
    public static DiscordDirectMessageResult Ok(string channelId) => new(true, channelId);
    public static DiscordDirectMessageResult Fail(string error, bool channelMissing = false) =>
        new(false, Error: error, ChannelMissing: channelMissing);
}

public sealed class DiscordCreateDmRequest
{
    public string RecipientId { get; set; } = "";
}

public sealed class DiscordChannelResponse
{
    public string Id { get; set; } = "";
}

public sealed class DiscordUserResponse
{
    public string Id { get; set; } = "";
    public string Username { get; set; } = "";
}
