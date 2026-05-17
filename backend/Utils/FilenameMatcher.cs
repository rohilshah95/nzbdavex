using System.Text.RegularExpressions;

namespace NzbWebDAV.Utils;

public static class FilenameMatcher
{
    private static readonly Regex BoundaryRegex = new(
        @"\b(\d{4}|S\d{1,2}(E\d{1,3})?|\d{3,4}p)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NonAlnumRegex = new(
        @"[^a-z0-9]+",
        RegexOptions.Compiled);

    public static string[] HeadTokens(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        var lower = s.ToLowerInvariant();
        var m = BoundaryRegex.Match(lower);
        while (m.Success && m.Index == 0) m = m.NextMatch();
        var head = m.Success ? lower[..m.Index] : lower;
        return NonAlnumRegex.Replace(head, " ")
                            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    public static bool TokensEqual(string[] a, string[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    public static bool Matches(string? query, string? candidate)
    {
        var q = HeadTokens(query);
        if (q.Length == 0) return true;
        return TokensEqual(q, HeadTokens(candidate));
    }
}
