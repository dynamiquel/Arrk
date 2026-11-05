using System.Reflection;

namespace Stackarr.Core.Modules;

/// <summary>
/// A <c>major.minor.revision</c> version number for Stackarr and its modules.
/// </summary>
public readonly record struct StackarrVersion(int Major, int Minor, int Revision)
{
    public static readonly StackarrVersion Zero = new(0, 0, 0);

    /// <summary>
    /// The running Stackarr version. Every built-in assembly (and therefore the app) shares the
    /// single <c>&lt;Version&gt;</c> value declared in the repository's <c>Directory.Build.props</c>.
    /// </summary>
    public static StackarrVersion Current => FromAssembly(typeof(StackarrVersion).Assembly);

    /// <summary>
    /// Reads the version stamped on <paramref name="assembly"/>, falling back to the assembly
    /// name version when no informational version is present.
    /// </summary>
    public static StackarrVersion FromAssembly(Assembly assembly)
    {
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (TryParse(informational, out StackarrVersion version))
        {
            return version;
        }

        Version? nameVersion = assembly.GetName().Version;
        if (nameVersion is not null)
        {
            return new StackarrVersion(nameVersion.Major, nameVersion.Minor, Math.Max(nameVersion.Build, 0));
        }

        return Zero;
    }

    /// <summary>Parses a version string, throwing on malformed input.</summary>
    public static StackarrVersion Parse(string value)
    {
        if (!TryParse(value, out StackarrVersion version))
        {
            throw new FormatException($"'{value}' is not a valid major.minor.revision version.");
        }

        return version;
    }

    /// <summary>
    /// Parses "<c>major.minor[.revision]</c>". Build metadata and pre-release suffixes
    /// (e.g. <c>0.1.0+abc</c>, <c>0.1.0-beta.1</c>) are ignored. A missing revision is treated
    /// as <c>0</c>.
    /// </summary>
    public static bool TryParse(string? value, out StackarrVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan().Trim();

        // Drop anything after the numeric version (build metadata / pre-release).
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] is '+' or '-')
            {
                span = span[..i];
                break;
            }
        }

        if (span.IsEmpty)
        {
            return false;
        }

        int[] parts = new int[3];
        int partIndex = 0;
        int start = 0;
        for (int i = 0; i <= span.Length; i++)
        {
            if (i == span.Length || span[i] == '.')
            {
                if (partIndex >= 3)
                {
                    return false;
                }

                if (!int.TryParse(span[start..i], out int parsed) || parsed < 0)
                {
                    return false;
                }

                parts[partIndex++] = parsed;
                start = i + 1;
            }
        }

        if (partIndex == 0)
        {
            return false;
        }

        version = new StackarrVersion(parts[0], parts[1], parts[2]);
        return true;
    }

    public override string ToString() => $"{Major}.{Minor}.{Revision}";
}
