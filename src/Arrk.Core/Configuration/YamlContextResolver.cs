namespace Arrk.Core.Configuration;

/// <summary>
/// Determines the editing context at a cursor position in YAML text.
/// Used by ConfigAutocompleteProvider for context-aware suggestions.
/// </summary>
public static class YamlContextResolver
{
    public static YamlContext GetContext(string yamlText, int cursorOffset)
    {
        if (string.IsNullOrEmpty(yamlText))
            return new() { IndentLevel = 0, ParentKeys = [], IsKeyContext = true };

        string[] lines = yamlText.Split('\n');
        int currentLineIndex = FindLineIndex(lines, cursorOffset);
        string currentLine = lines[currentLineIndex];
        int currentIndent = GetIndentLevel(currentLine);

        List<string> parentKeys = BuildParentKeyStack(lines, currentLineIndex, currentIndent);
        string currentTrimmed = currentLine.TrimStart();

        if (string.IsNullOrWhiteSpace(currentTrimmed))
        {
            return new()
            {
                IndentLevel = currentIndent,
                ParentKeys = parentKeys,
                IsKeyContext = true,
                IsAtLineStart = true,
                CurrentLineText = "",
                CurrentLineIndex = currentLineIndex
            };
        }

        int colonIndex = currentTrimmed.IndexOf(':');
        if (colonIndex < 0)
        {
            return new()
            {
                IndentLevel = currentIndent,
                ParentKeys = parentKeys,
                IsKeyContext = true,
                CurrentLineText = currentTrimmed,
                CurrentLineIndex = currentLineIndex
            };
        }

        string keyPart = currentTrimmed[..colonIndex].Trim().Trim('"', '\'');
        string valuePart = currentTrimmed[(colonIndex + 1)..].TrimStart();

        bool isListItem = currentTrimmed.TrimStart().StartsWith("- ");

        if (!string.IsNullOrEmpty(valuePart))
        {
            return new()
            {
                IndentLevel = currentIndent,
                ParentKeys = parentKeys,
                IsKeyContext = false,
                CurrentKey = keyPart,
                CurrentLineText = currentTrimmed,
                CurrentLineIndex = currentLineIndex,
                IsListItem = isListItem
            };
        }

        return new()
        {
            IndentLevel = currentIndent,
            ParentKeys = parentKeys,
            IsKeyContext = false,
            IsAfterColon = true,
            CurrentKey = keyPart,
            CurrentLineText = currentTrimmed,
            CurrentLineIndex = currentLineIndex,
            IsListItem = isListItem
        };
    }

    /// <summary>
    /// Get keys already used at the current indent level in the YAML text.
    /// </summary>
    public static HashSet<string> GetUsedKeys(string yamlText, YamlContext context)
    {
        HashSet<string> used = new();
        if (string.IsNullOrEmpty(yamlText))
            return used;

        string[] lines = yamlText.Split('\n');
        int targetIndent = context.IndentLevel;

        for (int i = 0; i < lines.Length; i++)
        {
            if (i == context.CurrentLineIndex)
                continue;

            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int indent = GetIndentLevel(line);
            if (indent != targetIndent)
                continue;

            string trimmed = line.TrimStart();

            // Handle list items: "- key: value" at the target indent
            if (trimmed.StartsWith("- "))
            {
                string afterDash = trimmed[2..];
                int colonIdx = afterDash.IndexOf(':');
                if (colonIdx >= 0)
                {
                    string key = afterDash[..colonIdx].Trim().Trim('"', '\'');
                    used.Add(key);
                }
                continue;
            }

            int colonIdx2 = trimmed.IndexOf(':');
            if (colonIdx2 >= 0)
            {
                string key = trimmed[..colonIdx2].Trim().Trim('"', '\'');
                used.Add(key);
            }
        }

        return used;
    }

    static int FindLineIndex(string[] lines, int cursorOffset)
    {
        int offset = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            int lineEnd = offset + lines[i].Length;
            if (cursorOffset <= lineEnd || i == lines.Length - 1)
                return i;
            offset = lineEnd + 1;
        }
        return lines.Length - 1;
    }

    static List<string> BuildParentKeyStack(string[] lines, int currentLineIndex, int currentIndent)
    {
        List<KeyEntry> stack = new();

        for (int i = 0; i < currentLineIndex; i++)
        {
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int indent = GetIndentLevel(line);
            string trimmed = line.TrimStart();

            // Handle list items: skip the "- " prefix to get the actual key
            if (trimmed.StartsWith("- "))
                trimmed = trimmed[2..];

            int colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0)
                continue;

            string key = trimmed[..colonIdx].Trim().Trim('"', '\'');

            while (stack.Count > 0 && stack[^1].Indent >= indent)
                stack.RemoveAt(stack.Count - 1);

            stack.Add(new() { Key = key, Indent = indent });
        }

        return stack.Select(e => e.Key).ToList();
    }

    static int GetIndentLevel(string line)
    {
        int count = 0;
        foreach (char c in line)
        {
            if (c == ' ') count++;
            else if (c == '\t') count += 4;
            else break;
        }
        return count;
    }

    class KeyEntry
    {
        public string Key { get; set; } = "";
        public int Indent { get; set; }
    }
}

/// <summary>
/// Editing context at a specific cursor position in a YAML document.
/// </summary>
public class YamlContext
{
    public int IndentLevel { get; set; }
    public List<string> ParentKeys { get; set; } = new();
    public bool IsKeyContext { get; set; }
    public bool IsAfterColon { get; set; }
    public bool IsAtLineStart { get; set; }
    public bool IsListItem { get; set; }
    public string CurrentKey { get; set; } = "";
    public string CurrentLineText { get; set; } = "";
    public int CurrentLineIndex { get; set; }
    public string SectionPath => string.Join(":", ParentKeys);
}
