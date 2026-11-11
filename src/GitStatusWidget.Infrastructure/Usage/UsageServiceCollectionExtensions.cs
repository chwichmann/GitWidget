using GitStatusWidget.Application.Usage;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace GitStatusWidget.Infrastructure.Usage;

/// <summary>Registers the Claude usage source (issue #17).</summary>
public static class UsageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IClaudeUsageSource"/> and its named <see cref="HttpClient"/> (no automatic redirects,
    /// redacted header logging).
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="options">Endpoint and credential locations; defaults to the Claude Code sign-in of the user.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddClaudeUsage(this IServiceCollection services, ClaudeUsageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(ClaudeUsageSource.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(ProviderHttpClient.CreateHandler)
            .RedactLoggedHeaders(static _ => true);
        services.TryAddSingleton<IClaudeUsageSource>(sp => new ClaudeUsageSource(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            options));
        return services;
    }
}
