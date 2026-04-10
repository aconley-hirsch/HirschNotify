using System.Text.RegularExpressions;

namespace HirschNotify.Services.Health;

/// <summary>
/// Tiny glob matcher for service-name / provider-name patterns. Supports <c>*</c>
/// (any run of characters) and <c>?</c> (single character). Everything else is
/// matched literally and case-insensitively.
/// </summary>
public static class WildcardMatcher
{
    public static bool ContainsWildcard(string pattern) =>
        pattern.Contains('*') || pattern.Contains('?');

    public static Regex Compile(string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    public static bool IsMatch(string pattern, string value)
    {
        if (!ContainsWildcard(pattern))
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
        return Compile(pattern).IsMatch(value);
    }
}
