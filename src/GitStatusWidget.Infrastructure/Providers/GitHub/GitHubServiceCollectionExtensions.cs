using GitStatusWidget.Application.Abstractions;
using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Infrastructure.Providers.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Providers.GitHub;

/// <summary>
/// Registers the GitHub provider adapter.
/// </summary>
public static class GitHubServiceCollectionExtensions
{
    /// <summary>
    /// Registers the named <see cref="HttpClient"/> <c>"GitHub"</c> (no automatic redirects, decompression,
    /// redacted header logging) and the GitHub <see cref="IRepositoryStatusProvider"/> as a singleton.
    /// Requires an <see cref="ICredentialStore"/> registration; <see cref="TimeProvider"/> defaults to the system clock.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddGitHubProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddLogging();
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpClient(GitHubStatusProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(ProviderHttpClient.CreateHandler)
            .RedactLoggedHeaders(static _ => true);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRepositoryStatusProvider, GitHubStatusProvider>(static sp => new GitHubStatusProvider(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<GitHubStatusProvider>>())));

        return services;
    }
}
