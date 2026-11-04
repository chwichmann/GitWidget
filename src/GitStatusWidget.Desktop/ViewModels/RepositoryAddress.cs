using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>Parses user input such as a pasted repository URL.</summary>
public static class RepositoryAddress
{
    /// <summary>
    /// Splits <c>https://host[/root]/group/sub/project[.git]</c> into server and path. For GitLab a relative URL root
    /// cannot be distinguished from a group, so only the host is taken as server unless <paramref name="knownServer"/>
    /// is a prefix of the URL.
    /// </summary>
    public static bool TryParseUrl(string input, Uri? knownServer, out Uri server, out string path, out RepositoryProviderKind? provider)
    {
        server = null!;
        path = string.Empty;
        provider = null;
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        var full = $"{url.Scheme}://{url.Authority}{url.AbsolutePath}".TrimEnd('/');
        if (full.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            full = full[..^4];
        }

        // Strip GitLab/GitHub sub-pages such as /-/issues or /issues.
        var marker = full.IndexOf("/-/", StringComparison.Ordinal);
        if (marker > 0)
        {
            full = full[..marker];
        }

        var root = knownServer is not null && full.StartsWith(knownServer.ToString().TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)
            ? knownServer.ToString().TrimEnd('/')
            : $"{url.Scheme}://{url.Authority}";
        path = Uri.UnescapeDataString(full[root.Length..].Trim('/'));
        if (url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            provider = RepositoryProviderKind.GitHub;
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            path = segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : path;
        }
        else if (url.Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase))
        {
            provider = RepositoryProviderKind.GitLab;
        }

        server = new Uri(root + "/");
        return path.Contains('/', StringComparison.Ordinal);
    }

    /// <summary>Splits <c>owner/sub/name</c> into owner path and name.</summary>
    public static (string Owner, string Name) Split(string path)
    {
        var trimmed = path.Trim().Trim('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? (string.Empty, trimmed) : (trimmed[..index], trimmed[(index + 1)..]);
    }
}
