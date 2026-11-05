namespace Stackarr.Ai;

/// <summary>One selectable assistant persona. Style affects tone only, never facts or permissions.</summary>
public sealed record AssistantPersona(
    string Key,
    string Name,
    string Description,
    string Style);

/// <summary>
/// Built-in and config-defined assistant personas. The plain persona is the safe default; the
/// character presets are tone instructions, not a licence to invent library facts or ignore the
/// assistant's action rules.
/// </summary>
public static class AiPersonas
{
    public const string Plain = "plain";

    /// <summary>Built-ins always exist, even when the config section is empty. Character personas
    /// beyond these are ordinary config entries so an administrator can edit or remove them.</summary>
    public static IReadOnlyList<AssistantPersona> BuiltIn { get; } =
    [
        new(Plain, "Plain",
            "Direct, warm and concise.",
            "Be direct, warm and concise. No role-play and no catchphrases."),
        new("jarvis", "Jarvis",
            "Calm, precise British AI with dry wit.",
            "Speak like JARVIS: calm, precise British composure, formal vocabulary and understated dry wit. Anticipate the next step, address the user politely, and deliver even bad news without panic."),
        new("cortana", "Cortana",
            "Warm, witty tactical AI.",
            "Speak like Cortana: quick, warm and quietly witty, with a tactical AI's confidence. Use dry humour and light teasing, stay concise in tense moments, and do not call the user 'Chief' unless they ask."),
    ];

    /// <summary>Built-ins first, then configured custom personas (duplicate keys are ignored).</summary>
    public static IReadOnlyList<AssistantPersona> All(AiConfig config)
    {
        List<AssistantPersona> personas = [.. BuiltIn];
        HashSet<string> keys = new(BuiltIn.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);

        foreach (AiPersonaDefinition custom in config.Personas.Custom)
        {
            string key = (custom.Key ?? "").Trim();
            if (key.Length == 0 || !keys.Add(key))
                continue;

            personas.Add(new AssistantPersona(
                key,
                string.IsNullOrWhiteSpace(custom.Name) ? key : custom.Name.Trim(),
                string.IsNullOrWhiteSpace(custom.Description) ? "" : custom.Description.Trim(),
                string.IsNullOrWhiteSpace(custom.Style) ? "" : custom.Style.Trim()));
        }

        return personas;
    }

    /// <summary>Resolves a stored key, then the configured default, then plain.</summary>
    public static AssistantPersona Resolve(AiConfig config, string? key)
    {
        IReadOnlyList<AssistantPersona> personas = All(config);
        if (!string.IsNullOrWhiteSpace(key))
        {
            AssistantPersona? exact = personas.FirstOrDefault(p =>
                string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
                return exact;
        }

        string defaultKey = string.IsNullOrWhiteSpace(config.Personas.Default)
            ? Plain
            : config.Personas.Default.Trim();
        return personas.FirstOrDefault(p =>
                   string.Equals(p.Key, defaultKey, StringComparison.OrdinalIgnoreCase))
               ?? personas[0];
    }

    public static bool IsKnown(AiConfig config, string? key) =>
        !string.IsNullOrWhiteSpace(key)
        && All(config).Any(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
