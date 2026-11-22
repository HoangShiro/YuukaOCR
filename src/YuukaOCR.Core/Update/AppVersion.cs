using System.Globalization;

namespace YuukaOCR.Core.Update;

/// <summary>
/// A version number that survives real-world release tags. GitHub tags are hand-written — <c>v2.1.0</c>,
/// <c>2.1</c>, <c>2.1.0.0</c>, <c>2.2.0-beta.1</c> and <c>2.2.0+build.7</c> all appear in the wild — so
/// parsing is lenient and comparison is strict.
///
/// Four components, missing ones zero, and a pre-release suffix is dropped from the comparison: a beta
/// of the next version is still "newer" than the installed release, which is what the update prompt
/// should say. <see cref="Version"/> could do the comparison but not the parsing.
/// </summary>
public readonly record struct AppVersion(int Major, int Minor, int Build, int Revision)
    : IComparable<AppVersion>
{
    public static readonly AppVersion Zero = new(0, 0, 0, 0);

    public static AppVersion From(Version? version) =>
        version is null
            ? Zero
            : new AppVersion(
                Math.Max(0, version.Major),
                Math.Max(0, version.Minor),
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision));

    /// <summary>
    /// Reads "v2.1.0", "2.1", "2.1.0.0", "2.2.0-beta.1". Returns false for anything that is not a
    /// dotted number — an unreadable tag must be reported, never silently treated as 0.0.0.
    /// </summary>
    public static bool TryParse(string? text, out AppVersion value)
    {
        value = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim();
        if (span[0] is 'v' or 'V') span = span[1..];

        // Build metadata and pre-release labels are not part of the ordering here.
        var cut = span.IndexOfAny(['-', '+']);
        if (cut > 0) span = span[..cut];
        span = span.Trim();
        if (span.Length == 0) return false;

        var parts = span.Split('.');
        if (parts.Length is < 1 or > 4) return false;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return false;
            numbers[i] = number;
        }

        value = new AppVersion(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Build != other.Build) return Build.CompareTo(other.Build);
        return Revision.CompareTo(other.Revision);
    }

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;
    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;
    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    /// <summary>Short form for the UI: "2.1.0", or "2.1.0.3" when a revision is set.</summary>
    public override string ToString() =>
        Revision > 0 ? $"{Major}.{Minor}.{Build}.{Revision}" : $"{Major}.{Minor}.{Build}";
}
