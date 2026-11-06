using System.Text.Json;

namespace Arrk.Core.Requests;

/// <summary>
/// Helpers for the provider-id dictionary carried on requests and blacklist entries. Provider ids
/// are the common language between the request system and every backend (Radarr/Sonarr/Lidarr/...),
/// so nothing here depends on a specific service.
/// </summary>
public static class ProviderIdMap
{
    public static string Serialize(IReadOnlyDictionary<string, string> ids)
    {
        Dictionary<string, string> clean = new();
        foreach ((string key, string value) in ids)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                clean[key] = value;
        }

        return JsonSerializer.Serialize(clean);
    }

    public static IReadOnlyDictionary<string, string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            Dictionary<string, string>? parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>True when two id dictionaries share at least one identical id.</summary>
    public static bool Overlaps(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        foreach ((string key, string value) in a)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (b.TryGetValue(key, out string? other) &&
                !string.IsNullOrWhiteSpace(other) &&
                string.Equals(other, value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds a clean id map from (key, value) pairs, skipping blanks.</summary>
    public static Dictionary<string, string> Build(params (string Key, string? Value)[] entries)
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string? value) in entries)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                map[key] = value!;
        }

        return map;
    }
}
