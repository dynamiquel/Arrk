using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stackarr.Ai;

/// <summary>Lenient JSON parsing for model answers (which may arrive fenced in Markdown).</summary>
public static class AiJson
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static T? TryParse<T>(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return default;

        string json = StripFence(text.Trim());
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        int firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewline)
            return text;

        return text[(firstNewline + 1)..lastFence].Trim();
    }
}
