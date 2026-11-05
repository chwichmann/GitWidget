using System.Globalization;

namespace GitStatusWidget.Application.Updates;

/// <summary>
/// Minimal semantic version (<c>major.minor.patch[-prerelease][+build]</c>) with SemVer 2.0 precedence.
/// </summary>
public sealed record SemanticVersion(int Major, int Minor, int Patch, string? Prerelease) : IComparable<SemanticVersion>
{
    /// <summary>Whether this is a pre-release version (has a <c>-suffix</c>).</summary>
    public bool IsPrerelease => !string.IsNullOrEmpty(Prerelease);

    /// <summary>Parses <c>1.2.3</c>, <c>v1.2.3-beta.1</c>, <c>1.2</c> (patch 0) or <c>1.2.3+sha</c>.</summary>
    /// <param name="text">Version text, optionally prefixed with <c>v</c>.</param>
    /// <param name="version">The parsed version.</param>
    /// <returns><see langword="true"/> when the text is a version.</returns>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            value = value[..plus];
        }

        string? prerelease = null;
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            prerelease = value[(dash + 1)..];
            value = value[..dash];
            if (prerelease.Length == 0)
            {
                return false;
            }
        }

        var parts = value.Split('.');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    /// <inheritdoc />
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        result = result != 0 ? result : Minor.CompareTo(other.Minor);
        result = result != 0 ? result : Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        // A version without pre-release has higher precedence than one with (1.0.0 > 1.0.0-rc.1).
        return (IsPrerelease, other.IsPrerelease) switch
        {
            (false, false) => 0,
            (false, true) => 1,
            (true, false) => -1,
            _ => ComparePrerelease(Prerelease!, other.Prerelease!),
        };
    }

    /// <inheritdoc />
    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    /// <summary>Greater-than comparison.</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left is not null && left.CompareTo(right) > 0;

    /// <summary>Less-than comparison.</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => right is not null && right.CompareTo(left) > 0;

    /// <summary>Greater-or-equal comparison.</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => !(left < right);

    /// <summary>Less-or-equal comparison.</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => !(left > right);

    private static int ComparePrerelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xNumeric = int.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
            var yNumeric = int.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
            var result = (xNumeric, yNumeric) switch
            {
                (true, true) => xn.CompareTo(yn),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(x[i], y[i]),
            };
            if (result != 0)
            {
                return result;
            }
        }

        return x.Length.CompareTo(y.Length);
    }
}
