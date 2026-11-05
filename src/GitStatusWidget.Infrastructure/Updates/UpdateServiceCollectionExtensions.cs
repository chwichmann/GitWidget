using GitStatusWidget.Application.Credentials;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Updates;
using GitStatusWidget.Infrastructure.Providers.GitHub;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace GitStatusWidget.Infrastructure.Updates;

/// <summary>Registers the release feed used by the update check (issue #7).</summary>
public static class UpdateServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IReleaseFeed"/> reading GitWidget releases from GitHub. Requires the GitHub provider
    /// (named HTTP client), an <see cref="ICredentialStore"/> and an <see cref="ISynchronizationController"/>.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="options">Feed options; defaults to <c>chwichmann/GitWidget</c> on github.com.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddReleaseFeed(this IServiceCollection services, ReleaseFeedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddGitHubProvider();
        services.TryAddSingleton<IReleaseFeed>(sp => new GitHubReleaseFeed(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<ISynchronizationController>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<GitHubReleaseFeed>>(),
            options));
        return services;
    }
}
