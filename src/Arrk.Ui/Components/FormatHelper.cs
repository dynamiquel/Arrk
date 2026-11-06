using System.Globalization;

namespace Arrk.Ui.Components;

/// <summary>
/// Shared formatting utilities used across multiple pages.
/// </summary>
public static class FormatHelper
{
    private static readonly string[] SizeUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>
    /// Formats a byte count into a human-readable string (e.g. "1.5 GB").
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < SizeUnits.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }
        return $"{value:F1} {SizeUnits[unitIndex]}";
    }

    /// <summary>
    /// Formats a bytes-per-second rate as "X.X MB/s".
    /// </summary>
    public static string FormatRate(double bytesPerSec) => $"{FormatBytes((long)bytesPerSec)}/s";

    /// <summary>
    /// Formats a DateTime as a human-readable "time since" string (e.g. "3 days", "2 months").
    /// </summary>
    public static string FormatTimeSince(DateTime dateTime)
    {
        TimeSpan span = DateTime.UtcNow - dateTime;
        if (span.TotalDays < 1)
        {
            return "today";
        }

        if (span.TotalDays < 30)
        {
            return $"{(int)span.TotalDays} days";
        }

        if (span.TotalDays < 365)
        {
            return $"{(int)(span.TotalDays / 30)} months";
        }

        return $"{(int)(span.TotalDays / 365)} years";
    }

    public static string FormatRelativeTime(DateTime dateTime)
    {
        TimeSpan span = DateTime.UtcNow - dateTime;
        if (span.TotalMinutes < 1)
        {
            return "just now";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes}m ago";
        }

        if (span.TotalDays < 1)
        {
            return $"{(int)span.TotalHours}h ago";
        }

        return $"{(int)span.TotalDays}d ago";
    }

    private static readonly Dictionary<string, string> UsToUkCertification = new(StringComparer.OrdinalIgnoreCase)
    {
        ["G"] = "U",
        ["PG"] = "PG",
        ["PG-13"] = "12A",
        ["R"] = "15",
        ["NC-17"] = "18",
        ["TV-Y"] = "U",
        ["TV-Y7"] = "PG",
        ["TV-G"] = "U",
        ["TV-PG"] = "PG",
        ["TV-14"] = "12A",
        ["TV-MA"] = "18"
    };

    private static readonly HashSet<string> UkCertifications = new(StringComparer.OrdinalIgnoreCase)
    {
        "U", "PG", "12A", "12", "15", "18", "R18", "15A"
    };

    /// <summary>Turns an ISO language code ("en", "ja") into a readable name ("English", "Japanese").</summary>
    public static string LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "Unknown";
        }

        // Already a display name (e.g. Radarr returns "English")?
        if (code.Length > 3 || code.Contains(' '))
        {
            return code;
        }

        try
        {
            string name = CultureInfo.GetCultureInfo(code).EnglishName;
            int paren = name.IndexOf('(');
            if (paren > 0)
            {
                name = name[..paren].Trim();
            }
            return string.IsNullOrEmpty(name) ? code : name;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Formats an ISO date (yyyy-MM-dd) as a readable date.</summary>
    public static string FormatReleaseDate(string? isoDate)
    {
        if (string.IsNullOrWhiteSpace(isoDate))
        {
            return "";
        }

        return DateTime.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date)
            ? date.ToString("MMM dd, yyyy")
            : isoDate;
    }

    /// <summary>Parses an ISO date (yyyy-MM-dd) into a <see cref="DateTime"/>, or null when unparseable.</summary>
    public static DateTime? ParseReleaseDate(string? isoDate) =>
        DateTime.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) ? date : null;

    /// <summary>True when the release date has already passed (date-only, compared against today).</summary>
    public static bool IsReleased(DateTime? date) => date.HasValue && date.Value.Date <= DateTime.UtcNow.Date;

    /// <summary>True when the ISO release date has already passed (date-only, compared against today).</summary>
    public static bool IsReleased(string? isoDate) => IsReleased(ParseReleaseDate(isoDate));

    /// <summary>
    /// The release window a movie is currently in, based on its release dates, or null when no
    /// window has opened yet. Later windows take precedence: physical → web → cinema.
    /// </summary>
    public static string? MovieReleaseStage(DateTime? inCinemas, DateTime? digital, DateTime? physical)
    {
        if (IsReleased(physical)) return "Physical Release";
        if (IsReleased(digital)) return "Web Release";
        if (IsReleased(inCinemas)) return "In Cinema";
        return null;
    }

    public static string ToUkCertification(string? certification)
    {
        if (string.IsNullOrWhiteSpace(certification))
        {
            return "";
        }

        if (UkCertifications.Contains(certification))
        {
            return certification;
        }

        return UsToUkCertification.TryGetValue(certification, out string? uk)
            ? uk
            : certification;
    }
}
