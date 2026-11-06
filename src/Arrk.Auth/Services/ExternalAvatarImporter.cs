using Microsoft.Extensions.Logging;

namespace Arrk.Auth.Services;

/// <summary>An upstream profile picture ready to be stored as an Arrk avatar.</summary>
public sealed record AvatarImage(byte[] Content, string ContentType);

/// <summary>
/// Downloads a profile picture from an upstream identity provider (Discord's OIDC
/// <c>picture</c> claim, MyAnimeList's <c>picture_url</c>). Remote failures never block sign-in:
/// the account is created with the normal initials placeholder instead.
/// </summary>
public sealed class ExternalAvatarImporter(
    IHttpClientFactory httpClientFactory,
    ILogger<ExternalAvatarImporter> logger)
{
    public const string ClientName = "arrk-external-avatar";
    public const int MaxBytes = 2 * 1024 * 1024;

    public async Task<AvatarImage?> DownloadAsync(string? url, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;

        try
        {
            HttpClient client = httpClientFactory.CreateClient(ClientName);
            using HttpResponseMessage response = await client.GetAsync(
                uri, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation(
                    "Upstream avatar request returned {Status}; keeping the existing profile picture",
                    (int)response.StatusCode);
                return null;
            }

            string? contentType = AllowedContentType(response.Content.Headers.ContentType?.MediaType);
            if (contentType is null)
                return null;

            if (response.Content.Headers.ContentLength is > MaxBytes)
                return null;

            await using Stream stream = await response.Content.ReadAsStreamAsync(ct);
            byte[] buffer = new byte[MaxBytes + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (read == 0)
                    break;
                total += read;
            }

            if (total == 0 || total > MaxBytes)
                return null;

            return new AvatarImage(buffer[..total], contentType);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogWarning(ex, "Could not download the upstream profile picture");
            return null;
        }
    }

    // Only bitmap formats the account uploader also accepts. SVG is deliberately excluded: the
    // avatar endpoint serves the stored type inline, and an SVG can carry script.
    static string? AllowedContentType(string? mediaType)
        => mediaType?.ToLowerInvariant() switch
        {
            "image/png" => "image/png",
            "image/jpeg" => "image/jpeg",
            "image/webp" => "image/webp",
            "image/gif" => "image/gif",
            _ => null
        };
}
