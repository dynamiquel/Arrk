namespace Arrk.Core.Notifications;

/// <summary>
/// Hierarchical topic matching used to filter events, route notifications and resolve topic toggles
/// by their dotted topic. Supports the standard wildcards:
/// <list type="bullet">
/// <item><description><c>+</c> matches exactly one level, e.g. <c>downloads.+</c> matches <c>downloads.completed</c> but not <c>downloads.torrent.added</c>.</description></item>
/// <item><description><c>#</c> matches zero or more trailing levels, e.g. <c>system.health.#</c> matches <c>system.health.service.down</c> and <c>system.health</c>. <c>*</c> is an alias for <c>#</c>.</description></item>
/// </list>
/// A pattern with no wildcard is treated as a prefix: <c>downloads</c> matches <c>downloads</c> and
/// anything below it. Matching is case-insensitive. A blank pattern matches everything.
/// </summary>
public static class TopicFilter
{
    /// <summary>True when <paramref name="topic"/> satisfies <paramref name="pattern"/>.</summary>
    public static bool Matches(string? pattern, string? topic)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        if (string.IsNullOrWhiteSpace(topic))
            return false;

        string trimmedPattern = pattern.Trim();
        string trimmedTopic = topic.Trim();

        // No wildcards: treat as an exact topic or a parent prefix.
        if (!HasWildcard(trimmedPattern))
        {
            return trimmedTopic.Equals(trimmedPattern, StringComparison.OrdinalIgnoreCase)
                || trimmedTopic.StartsWith(trimmedPattern + ".", StringComparison.OrdinalIgnoreCase);
        }

        string[] patternLevels = trimmedPattern.Split('.');
        string[] topicLevels = trimmedTopic.Split('.');

        for (int i = 0; i < patternLevels.Length; i++)
        {
            string level = patternLevels[i];

            if (IsAnyDepth(level))
                return true; // matches the rest of the topic, including nothing

            if (i >= topicLevels.Length)
                return false;

            if (level != "+" && !level.Equals(topicLevels[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return patternLevels.Length == topicLevels.Length;
    }

    /// <summary>True when the pattern contains a topic wildcard (<c>+</c>, <c>#</c> or <c>*</c>).</summary>
    public static bool HasWildcard(string? pattern) =>
        !string.IsNullOrEmpty(pattern)
        && (pattern.Contains('+') || pattern.Contains('#') || pattern.Contains('*'));

    /// <summary>
    /// True when both patterns match the same topic but <paramref name="candidate"/> is the more
    /// specific one. Used to pick a winner when several routing keys or topic toggles match.
    /// </summary>
    public static bool IsMoreSpecific(string candidate, string current) =>
        CompareSpecificity(candidate, current) > 0;

    /// <summary>
    /// Compares two patterns by specificity; a positive result means <paramref name="x"/> is more
    /// specific. Deeper patterns win, then patterns with more literal levels, then fewer
    /// "any depth" wildcards, then the longer pattern (case-insensitively).
    /// </summary>
    public static int CompareSpecificity(string x, string y)
    {
        (int Levels, int Literals, int AnyDepth) a = Specificity(x);
        (int Levels, int Literals, int AnyDepth) b = Specificity(y);

        if (a.Levels != b.Levels)
            return a.Levels.CompareTo(b.Levels);

        if (a.Literals != b.Literals)
            return a.Literals.CompareTo(b.Literals);

        if (a.AnyDepth != b.AnyDepth)
            return b.AnyDepth.CompareTo(a.AnyDepth);

        if (x.Length != y.Length)
            return x.Length.CompareTo(y.Length);

        return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }

    static (int Levels, int Literals, int AnyDepth) Specificity(string pattern)
    {
        string[] levels = pattern.Trim().Split('.');
        int literals = 0;
        int anyDepth = 0;

        foreach (string level in levels)
        {
            if (IsAnyDepth(level))
                anyDepth++;
            else if (level != "+")
                literals++;
        }

        return (levels.Length, literals, anyDepth);
    }

    static bool IsAnyDepth(string level) => level is "#" or "*";
}
