using System.Text.Json;
using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Settings;
using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Providers;

/// <summary>
/// Shared helpers that convert exceptions at the provider boundary into classified results.
/// </summary>
internal static class ProviderBoundary
{
    public static async Task<ProviderFetchResult> RunAsync(
        Func<Task<ProviderFetchResult.Success>> fetch,
        Func<RateLimitInfo?> rateLimit,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await fetch().ConfigureAwait(false);
            return result with { RateLimit = rateLimit() };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderFailureException ex)
        {
            return new ProviderFetchResult.Failure(ex.Kind, ex.Message, ex.RetryAfter) { RateLimit = rateLimit() };
        }
        catch (ProviderResponseException ex)
        {
            return new ProviderFetchResult.Failure(ProviderFailureKind.Malformed, ex.Message) { RateLimit = rateLimit() };
        }
        catch (JsonException ex)
        {
            return new ProviderFetchResult.Failure(ProviderFailureKind.Malformed, $"Invalid JSON: {ex.Message}") { RateLimit = rateLimit() };
        }
        catch (FormatException ex)
        {
            return new ProviderFetchResult.Failure(ProviderFailureKind.Malformed, $"Unexpected value: {ex.Message}") { RateLimit = rateLimit() };
        }
    }

    public static async Task<ProviderConnectionResult> RunConnectionAsync(Func<Task<ProviderConnectionResult>> test, CancellationToken cancellationToken)
    {
        try
        {
            return await test().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ProviderFailureException ex)
        {
            return ProviderConnectionResult.Fail(ex.Kind, ex.Message);
        }
        catch (Exception ex) when (ex is ProviderResponseException or JsonException or FormatException)
        {
            return ProviderConnectionResult.Fail(ProviderFailureKind.Malformed, ex.Message);
        }
    }

    /// <summary>Loads the token just before use (architecture §9.2, §18.2).</summary>
    public static async Task<Secret> LoadTokenAsync(ICredentialStore store, RepositoryConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!store.IsAvailable)
        {
            throw new ProviderFailureException(ProviderFailureKind.Configuration, $"{store.StoreName} is not available on this system.");
        }

        return await store.ReadAsync(configuration.CredentialReference, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderFailureException(ProviderFailureKind.AuthenticationFailed, "No token is stored for this repository.");
    }

    /// <summary>Validates the configured server URI (https only unless explicitly allowed).</summary>
    public static Uri ValidateServerUri(RepositoryConfiguration configuration)
    {
        var uri = configuration.ServerUri;
        if (!uri.IsAbsoluteUri)
        {
            throw new ProviderFailureException(ProviderFailureKind.Configuration, "The server address must be an absolute URL.");
        }

        var insecureAllowed = configuration.AllowInsecureHttp && uri.Scheme == Uri.UriSchemeHttp;
        if (uri.Scheme != Uri.UriSchemeHttps && !insecureAllowed)
        {
            throw new ProviderFailureException(ProviderFailureKind.Configuration, "Only https server addresses are allowed (http must be enabled explicitly).");
        }

        // Normalize to a trailing slash so relative paths resolve below a relative URL root.
        return uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.GetLeftPart(UriPartial.Path) + "/");
    }

    /// <summary>Effective User-Agent of a configuration.</summary>
    public static string UserAgent(RepositoryConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration.UserAgent) ? DefaultUserAgent : configuration.UserAgent.Trim();

    /// <summary>Default User-Agent, <c>GitWidget/&lt;version&gt;</c>.</summary>
    public static string DefaultUserAgent { get; } =
        $"GitWidget/{typeof(ProviderBoundary).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";
}
