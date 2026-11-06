namespace Arrk.Mal.Tools;

/// <summary>
/// Scores a spoken title against MAL's title fields. English/primary/display titles outrank
/// synonyms, exact beats prefix, and punctuation/case are ignored. The assistant uses this to avoid
/// silently picking a different cours when a title is ambiguous.
/// </summary>
internal static class MalTitleMatcher
{
    public static int Score(string wanted, params string?[] candidates)
    {
        string target = Normalize(wanted);
        if (target.Length == 0)
            return 0;

        int best = 0;
        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            foreach (string part in candidate.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string value = Normalize(part);
                if (value.Length == 0)
                    continue;

                if (value == target)
                    best = Math.Max(best, 100);
                else if (value.StartsWith(target, StringComparison.Ordinal) || target.StartsWith(value, StringComparison.Ordinal))
                    best = Math.Max(best, 70);
                else if (value.Contains(target, StringComparison.Ordinal) || target.Contains(value, StringComparison.Ordinal))
                    best = Math.Max(best, 50);
            }
        }

        return best;
    }

    static string Normalize(string? value) =>
        new((value ?? "").Where(char.IsLetterOrDigit).Select(c => char.ToLowerInvariant(c)).ToArray());
}
