using System.Text.RegularExpressions;

namespace ApimMigrator.Services;

public static class PolicyHelper
{
    public static readonly Regex RewriteUri =
        new(@"(<rewrite-uri\b[^>]*?\btemplate\s*=\s*"")([^""]*)("")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly Regex BackendBaseUrl =
        new(@"(<set-backend-service\b[^>]*?\bbase-url\s*=\s*"")([^""]*)("")", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NamedValue = new(@"\{\{([^}]+)\}\}", RegexOptions.Compiled);
    private static readonly Regex BackendId = new(@"backend-id\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? GetValue(Regex regex, string? xml)
    {
        if (xml is null) return null;
        var m = regex.Match(xml);
        return m.Success ? m.Groups[2].Value : null;
    }

    public static string? SetValue(Regex regex, string? xml, string? value)
    {
        if (xml is null || value is null) return xml;
        return regex.Replace(xml, m => m.Groups[1].Value + value + m.Groups[3].Value, 1);
    }

    public static IEnumerable<string> GetNamedValues(string? xml) =>
        xml is null ? [] : NamedValue.Matches(xml).Select(m => m.Groups[1].Value).Distinct();

    public static IEnumerable<string> GetBackendIds(string? xml) =>
        xml is null ? [] : BackendId.Matches(xml).Select(m => m.Groups[1].Value).Distinct();
}
