namespace GitStatusWidget.Infrastructure.Providers.Http;

/// <summary>
/// Parses RFC 8288 <c>Link</c> headers such as <c>&lt;https://…&gt;; rel="next", &lt;https://…&gt;; rel="last"</c>.
/// </summary>
internal static class LinkHeader
{
    /// <summary>Returns the URI of the given relation, or <see langword="null"/>.</summary>
    public static Uri? GetRelation(string? header, string relation)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        foreach (var part in SplitLinks(header))
        {
            var open = part.IndexOf('<', StringComparison.Ordinal);
            var close = part.IndexOf('>', StringComparison.Ordinal);
            if (open < 0 || close <= open)
            {
                continue;
            }

            var parameters = part[(close + 1)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            foreach (var parameter in parameters)
            {
                var eq = parameter.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0 || !parameter[..eq].Trim().Equals("rel", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rels = parameter[(eq + 1)..].Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (rels.Contains(relation, StringComparer.OrdinalIgnoreCase)
                    && Uri.TryCreate(part[(open + 1)..close].Trim(), UriKind.Absolute, out var uri))
                {
                    return uri;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SplitLinks(string header)
    {
        // Commas may occur inside <...>; split only on commas outside angle brackets.
        var depth = 0;
        var start = 0;
        for (var i = 0; i < header.Length; i++)
        {
            switch (header[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ',' when depth == 0:
                    yield return header[start..i];
                    start = i + 1;
                    break;
            }
        }

        yield return header[start..];
    }
}
