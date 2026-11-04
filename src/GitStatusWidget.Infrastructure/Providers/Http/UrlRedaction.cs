namespace GitStatusWidget.Infrastructure.Providers.Http;

/// <summary>
/// Removes potentially secret parts from URLs before they are logged or shown.
/// </summary>
internal static class UrlRedaction
{
    private static readonly string[] SensitiveParameters =
        ["private_token", "access_token", "token", "job_token", "client_secret", "code", "sig", "signature", "key"];

    public static string Redact(Uri? uri)
    {
        if (uri is null)
        {
            return string.Empty;
        }

        if (!uri.IsAbsoluteUri)
        {
            return uri.OriginalString;
        }

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        if (!string.IsNullOrEmpty(builder.Query))
        {
            var parts = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p =>
                {
                    var eq = p.IndexOf('=', StringComparison.Ordinal);
                    var name = eq < 0 ? p : p[..eq];
                    return SensitiveParameters.Contains(Uri.UnescapeDataString(name), StringComparer.OrdinalIgnoreCase)
                        ? $"{name}=***"
                        : p;
                });
            builder.Query = string.Join('&', parts);
        }

        return builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped);
    }
}
